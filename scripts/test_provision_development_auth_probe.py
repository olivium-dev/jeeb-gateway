"""Offline safety regressions. Never contacts providers or invokes credential CLIs."""
import base64
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

SCRIPT = Path(__file__).with_name("provision-development-auth-probe.py")
spec = importlib.util.spec_from_file_location("provision_subject", SCRIPT)
subject = importlib.util.module_from_spec(spec)
spec.loader.exec_module(subject)
NOW = 1800000000
HEAD = "a" * 40
NUMBER = "123456789"
OAUTH = "synthetic-oauth-value"
KEY = "fixture_" * 4
TOKEN = "synthetic-id-token-value"


def encoded(value):
    return json.dumps(value).encode()


def app():
    return {"packageName": subject.PACKAGE, "state": "ACTIVE", "appId": "1:123:android:abc",
            "projectId": subject.PROJECT,
            "name": "projects/" + NUMBER + "/androidApps/1:123:android:abc"}


def config():
    return {"project_info": {"project_id": subject.PROJECT, "project_number": NUMBER},
            "client": [{"client_info": {"mobilesdk_app_id": app()["appId"],
                        "android_client_info": {"package_name": subject.PACKAGE}},
                        "api_key": [{"current_key": KEY}]}]}


def minter_output(uid, **changes):
    probe = {"idToken": TOKEN, "expectedSubject": uid, "expectedProvider": "password"}
    evidence = {"status": "probe_minted", "project": subject.PROJECT, "provider": "password",
                "uidSha256Prefix": hashlib.sha256(uid.encode()).hexdigest()[:16],
                "tokenSha256Prefix": hashlib.sha256(TOKEN.encode()).hexdigest()[:16],
                "expiresAt": NOW + 3500}
    evidence.update(changes)
    return 0, encoded(probe), encoded(evidence)


class OfflineCase(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.network = patch.object(subject.http.client, "HTTPSConnection", side_effect=AssertionError("network forbidden"))
        self.network.start()
        self.addCleanup(self.network.stop)

    def worker(self, **kwargs):
        kwargs.setdefault("command", Mock(side_effect=AssertionError("unexpected command")))
        kwargs.setdefault("request", Mock(side_effect=AssertionError("unexpected provider request")))
        worker = subject.Provisioner(root=self.root, state=self.root / "state", now=lambda: NOW, **kwargs)
        worker.minter_source = SCRIPT.with_name("mint-development-firebase-diagnostic-probe.py").read_bytes()
        return worker

    def reject(self, reason, operation):
        with self.assertRaisesRegex(subject.Rejected, "^" + reason + "$"):
            operation()


class ApprovalTests(OfflineCase):
    def test_each_owner_flag_is_required_before_any_work(self):
        good = list(subject.EXECUTION_FLAGS)
        for args in [[], good + ["--unknown"], good + good[:1]] + [good[:i] + good[i+1:] for i in range(len(good))]:
            with self.subTest(args=args):
                worker = self.worker()
                worker.execute = Mock()
                output = io.StringIO()
                self.assertEqual(1, subject.run(["script"] + args, provisioner=worker, stdout=output, isolated=True))
                worker.execute.assert_not_called()
                self.assertEqual("explicit_owner_approvals_required", json.loads(output.getvalue())["reason"])

    def test_isolation_is_required_even_with_all_flags(self):
        worker = self.worker()
        worker.execute = Mock()
        output = io.StringIO()
        self.assertEqual(1, subject.run(["script"] + list(subject.EXECUTION_FLAGS), provisioner=worker,
                                       stdout=output, isolated=False))
        worker.execute.assert_not_called()
        self.assertEqual("isolated_mode_required", json.loads(output.getvalue())["reason"])

    def test_ambient_tls_overrides_rejected_before_work(self):
        for name in ("SSL_CERT_FILE", "SSL_CERT_DIR", "SSLKEYLOGFILE"):
            worker = self.worker()
            worker.execute = Mock()
            output = io.StringIO()
            with patch.dict(os.environ, {name: "/synthetic/untrusted/path"}):
                self.assertEqual(1, subject.run(["script"] + list(subject.EXECUTION_FLAGS), provisioner=worker, stdout=output, isolated=True))
            worker.execute.assert_not_called()
            self.assertEqual("ambient_tls_override_rejected", json.loads(output.getvalue())["reason"])

    def test_valid_approvals_execute_once(self):
        worker = self.worker()
        worker.execute = Mock(return_value={"status": "synthetic_complete"})
        self.assertEqual(0, subject.run(["script"] + list(subject.EXECUTION_FLAGS), provisioner=worker,
                                       stdout=io.StringIO(), isolated=True))
        worker.execute.assert_called_once_with()

    def test_unknown_exception_text_never_reaches_output(self):
        for error in (RuntimeError("synthetic-secret/credential"), subject.Rejected("synthetic_secret_credential")):
            with self.subTest(error=type(error).__name__):
                worker = self.worker()
                worker.execute = Mock(side_effect=error)
                output = io.StringIO()
                self.assertEqual(1, subject.run(["script"] + list(subject.EXECUTION_FLAGS), provisioner=worker,
                                               stdout=output, isolated=True))
                self.assertEqual("execution_failed", json.loads(output.getvalue())["reason"])
                self.assertNotIn("synthetic_secret", output.getvalue())


class SourceTests(OfflineCase):
    def setUp(self):
        super().setUp()
        (self.root / "scripts").mkdir()
        self.path = self.root / "scripts/mint-development-firebase-diagnostic-probe.py"
        self.blob = b"# inert synthetic source\n"
        self.path.write_bytes(self.blob)
        self.path.chmod(0o600)

    def source_worker(self, **changes):
        values = {"head": HEAD, "local_head": HEAD, "protected": True, "dirty": b"", "blob": self.blob}
        values.update(changes)
        def command(args, **kwargs):
            if args[1:2] == ["api"]:
                return 0, encoded({"protected": values["protected"], "commit": {"sha": values["head"]}}), b""
            if args[1:2] == ["rev-parse"]:
                return 0, values["local_head"].encode() + b"\n", b""
            if args[1:2] == ["status"]:
                return 0, values["dirty"], b""
            if args[1:2] == ["show"]:
                self.assertEqual(HEAD + ":scripts/mint-development-firebase-diagnostic-probe.py", args[2])
                return 0, values["blob"], b""
            self.fail("unexpected source command")
        return self.worker(command=command)

    def test_source_requires_protected_exact_clean_head_and_pinned_bytes(self):
        with patch.object(subject, "MINTER_HASH", hashlib.sha256(self.blob).hexdigest()):
            self.assertEqual(HEAD, self.source_worker().source_gate())
            for changes, reason in [({"protected": False}, "source_not_protected"),
                                    ({"local_head": "b" * 40}, "source_head_mismatch"),
                                    ({"dirty": b"?? injected.py\n"}, "source_tree_dirty"),
                                    ({"blob": b"different"}, "minter_source_mismatch")]:
                with self.subTest(changes=changes):
                    self.reject(reason, self.source_worker(**changes).source_gate)
        self.reject("minter_source_mismatch", self.source_worker().source_gate)

    def test_minter_symlink_and_writable_source_are_rejected(self):
        self.path.chmod(0o622)
        self.reject("minter_file_unsafe", self.source_worker().source_gate)
        self.path.unlink()
        other = self.root / "other.py"
        other.write_bytes(self.blob)
        self.path.symlink_to(other)
        self.reject("minter_file_unsafe", self.source_worker().source_gate)


class DiscoveryTests(OfflineCase):
    def discover(self, apps=None, client_config=None, next_token=None):
        selected = [app()] if apps is None else apps
        conf = config() if client_config is None else client_config
        calls = []
        def request(host, method, path, token, body=None):
            calls.append((host, method, path, token, body))
            self.assertEqual(("firebase.googleapis.com", "GET", OAUTH, None), (host, method, token, body))
            if path.endswith("/config"):
                return {"configFileContents": base64.b64encode(encoded(conf)).decode()}
            result = {"apps": selected}
            if next_token:
                result["nextPageToken"] = next_token
            return result
        return self.worker(request=request), calls

    def test_exact_app_and_config_select_one_key(self):
        unrelated = app()
        unrelated["packageName"] = "app.unrelated"
        worker, calls = self.discover(apps=[unrelated, app()])
        self.assertEqual(KEY, worker.app_key(NUMBER, OAUTH))
        self.assertEqual(2, len(calls))

    def test_ambiguous_or_missing_app_stops_without_fetching_config(self):
        for selected in ([], [app(), app()]):
            worker, calls = self.discover(apps=selected)
            self.reject("development_app_ambiguous", lambda: worker.app_key(NUMBER, OAUTH))
            self.assertEqual(1, len(calls))

    def test_cross_project_app_is_rejected(self):
        wrong = app()
        wrong["projectId"] = "unrelated-project"
        worker, calls = self.discover(apps=[wrong])
        self.reject("app_project_mismatch", lambda: worker.app_key(NUMBER, OAUTH))
        self.assertEqual(1, len(calls))

    def test_app_pagination_cap_never_selects_partial_results(self):
        worker, calls = self.discover(apps=[], next_token="repeated/synthetic-token")
        self.reject("apps_pagination_incomplete", lambda: worker.app_key(NUMBER, OAUTH))
        self.assertEqual(subject.MAX_PAGES, len(calls))
        self.assertIn("repeated%2Fsynthetic-token", calls[-1][2])

    def test_config_mismatch_and_key_ambiguity_fail_closed(self):
        variants = []
        wrong = config(); wrong["project_info"]["project_id"] = "unrelated"; variants.append((wrong, "config_project_mismatch"))
        wrong = config(); wrong["project_info"]["project_number"] = "999"; variants.append((wrong, "config_project_mismatch"))
        wrong = config(); wrong["client"][0]["client_info"]["mobilesdk_app_id"] = "unrelated"; variants.append((wrong, "config_app_mismatch"))
        wrong = config(); wrong["client"].append(copy.deepcopy(wrong["client"][0])); variants.append((wrong, "config_client_ambiguous"))
        wrong = config(); wrong["client"][0]["client_info"]["android_client_info"]["package_name"] = "unrelated"; variants.append((wrong, "config_client_ambiguous"))
        for keys in ([], [{"current_key": KEY}, {"current_key": KEY}]):
            wrong = config(); wrong["client"][0]["api_key"] = keys; variants.append((wrong, "config_key_ambiguous"))
        for conf, reason in variants:
            with self.subTest(reason=reason):
                worker, calls = self.discover(client_config=conf)
                self.reject(reason, lambda: worker.app_key(NUMBER, OAUTH))
                self.assertEqual(2, len(calls))

    def test_project_identity_and_active_state_are_required(self):
        for project in ({"projectId": "unrelated", "lifecycleState": "ACTIVE", "projectNumber": NUMBER},
                        {"projectId": subject.PROJECT, "lifecycleState": "DELETE_REQUESTED", "projectNumber": NUMBER}):
            command = Mock(return_value=(0, encoded(project), b""))
            self.reject("development_project_mismatch", self.worker(command=command).project_gate)
            self.assertEqual(1, command.call_count)

    def test_permission_preflight_is_targeted_and_requires_every_permission(self):
        required = sorted(subject.REQUIRED_PERMISSIONS)
        for permissions in (required, required[:-1], None):
            request = Mock(return_value={"permissions": permissions})
            worker = self.worker(request=request)
            if permissions == required:
                worker.permission_gate(OAUTH)
            else:
                self.reject("required_permissions_missing", lambda: worker.permission_gate(OAUTH))
            request.assert_called_once_with("cloudresourcemanager.googleapis.com", "POST",
                "/v1/projects/" + subject.PROJECT + ":testIamPermissions", OAUTH,
                {"permissions": required})

    def test_disabled_or_passwordless_provider_is_rejected_without_write(self):
        for email in ({"enabled": False, "passwordRequired": True}, {"enabled": True, "passwordRequired": False}, {}):
            request = Mock(return_value={"name": "projects/" + subject.PROJECT + "/config", "signIn": {"email": email}})
            self.reject("password_provider_not_enabled", lambda: self.worker(request=request).provider_gate(OAUTH, NUMBER))
            self.assertEqual(1, request.call_count)
            self.assertEqual("GET", request.call_args.args[1])


class SecretGateTests(OfflineCase):
    def test_repository_scope_pagination_must_complete(self):
        worker = self.worker()
        paths = []
        def gh(path, **kwargs):
            paths.append(path)
            if path.endswith("/environments/" + subject.ENVIRONMENT):
                return {"deployment_branch_policy": {"protected_branches": True, "custom_branch_policies": False}}
            if "/repositories?" in path:
                return {"repositories": [{"full_name": subject.REPO}] * 100}
            if path.endswith(subject.PROBE_SECRET):
                return None
            return {"visibility": "selected", "name": subject.SIGNING_SECRET}
        worker.gh = gh
        self.reject("transport_scope_unproved", worker.secret_gate)
        self.assertEqual(subject.MAX_PAGES, sum("/repositories?" in p for p in paths))

    def test_environment_must_allow_protected_branches_only(self):
        for policy in (None, {}, {"protected_branches": False, "custom_branch_policies": False},
                       {"protected_branches": True, "custom_branch_policies": True}):
            worker = self.worker()
            worker.gh = Mock(return_value={"deployment_branch_policy": policy})
            self.reject("environment_branch_policy_unproved", worker.secret_gate)
            self.assertEqual(1, worker.gh.call_count)

    def test_existing_environment_probe_fails_without_overwrite(self):
        worker = self.worker()
        worker.gh = Mock(return_value={"name": subject.PROBE_SECRET})
        self.reject("existing_probe_requires_manual_reconciliation", worker.require_probe_absent)
        worker.command.assert_not_called()
        worker.request.assert_not_called()

    def test_shadow_probe_is_rejected_after_transport_scope_proof(self):
        worker = self.worker()
        def gh(path, **kwargs):
            if path.endswith("/environments/" + subject.ENVIRONMENT):
                return {"deployment_branch_policy": {"protected_branches": True, "custom_branch_policies": False}}
            if "/repositories?" in path:
                return {"repositories": [{"full_name": subject.REPO}]}
            if "/environments/" in path and path.endswith(subject.PROBE_SECRET):
                return None
            return {"visibility": "selected", "name": subject.SIGNING_SECRET if path.endswith(subject.SIGNING_SECRET) else subject.PROBE_SECRET}
        worker.gh = gh
        self.reject("probe_shadow_present", worker.secret_gate)


class InterlockAndExecutionTests(OfflineCase):
    def prepared(self, fail_create=False, fail_mint=False, fail_write=False, mint_response=None):
        calls = []
        captured = {}
        worker = self.worker()
        worker.source_gate = Mock(return_value=HEAD)
        worker.secret_gate = Mock()
        worker.project_gate = Mock(return_value=(NUMBER, OAUTH))
        worker.app_key = Mock(return_value=KEY)
        worker.provider_gate = Mock()
        worker.permission_gate = Mock()
        worker.require_probe_absent = Mock()
        def request(host, method, path, token, body=None):
            calls.append("create")
            self.assertTrue((worker.state / "attempt.json").is_file())
            self.assertEqual(("identitytoolkit.googleapis.com", "POST", "/v1/accounts:signUp", OAUTH), (host, method, path, token))
            self.assertEqual(subject.PROJECT, body["targetProjectId"])
            self.assertRegex(body["localId"], r"^row03-[a-f0-9]{48}$")
            self.assertTrue(body["email"].endswith("@row03.invalid"))
            captured.update(body)
            return {"localId": "ambiguous" if fail_create else body["localId"]}
        def command(args, **kwargs):
            if args[0] == "/usr/bin/python3":
                calls.append("mint")
                stream = kwargs["stdin_file"]
                info = os.fstat(stream.fileno())
                self.assertTrue(stat.S_ISREG(info.st_mode))
                self.assertEqual(0o600, stat.S_IMODE(info.st_mode))
                self.assertEqual(0, info.st_nlink)
                value = json.load(stream)
                for secret in (value["password"], value["email"], value["webApiKey"]):
                    self.assertNotIn(secret, repr(args))
                self.assertNotIn("env", kwargs)
                self.assertEqual(captured["localId"], value["expectedUid"])
                if fail_mint:
                    return 1, b"synthetic-secret-output", b"synthetic-secret-error"
                if mint_response is not None:
                    return mint_response(value["expectedUid"])
                return minter_output(value["expectedUid"])
            if args[1:3] == ["secret", "set"]:
                self.assertEqual(["/opt/homebrew/bin/gh", "secret", "set",
                    "JEEB_DEVELOPMENT_FIREBASE_DIAGNOSTIC_PROBE_JSON", "--repo",
                    "olivium-dev/jeeb-gateway", "--env", "development-msi-gateway-signing"], args)
                calls.append("write")
                self.assertNotIn(TOKEN, repr(args))
                self.assertEqual(TOKEN, json.loads(kwargs["data"])["idToken"])
                return (1 if fail_write else 0), b"", b""
            if args[1:2] == ["api"]:
                calls.append("metadata")
                return 0, encoded({"name": subject.PROBE_SECRET}), b""
            self.fail("unexpected command")
        worker.command = command
        worker.request = request
        return worker, calls, captured

    def test_success_is_one_create_then_one_mint_then_one_secret_write(self):
        worker, calls, captured = self.prepared()
        output = io.StringIO()
        self.assertEqual(0, subject.run(["script"] + list(subject.EXECUTION_FLAGS), provisioner=worker, stdout=output, isolated=True))
        self.assertEqual(["create", "mint", "write", "metadata"], calls)
        result = json.loads(output.getvalue())
        self.assertFalse(result["workflowDispatched"])
        self.assertTrue(result["fixtureRetained"])
        for secret in (captured["password"], captured["email"], captured["localId"], TOKEN, OAUTH, KEY):
            self.assertNotIn(secret, output.getvalue())
            self.assertNotIn(secret, (worker.state / "attempt.json").read_text())

    def test_destination_mutation_cannot_pass_literal_success_contract(self):
        worker, calls, _ = self.prepared()
        with patch.object(subject, "ENVIRONMENT", "staging"):
            with self.assertRaises(AssertionError):
                worker.execute()
        self.assertEqual(["create", "mint"], calls)

    def test_malformed_minter_results_never_write_secret(self):
        cases = {
            "empty": lambda uid: (0, b"", b""),
            "wrong_project": lambda uid: minter_output(uid, project="synthetic-other-project"),
            "invalid_probe_shape": lambda uid: (0, encoded([]), minter_output(uid)[2]),
            "missing_probe_field": lambda uid: (0, encoded({"idToken": TOKEN}), minter_output(uid)[2]),
            "invalid_evidence_shape": lambda uid: (0, minter_output(uid)[1], encoded([])),
        }
        for label, response in cases.items():
            with self.subTest(label=label):
                worker, calls, _ = self.prepared(mint_response=response)
                worker.state = self.root / label
                output = io.StringIO()
                self.assertEqual(1, subject.run(["script"] + list(subject.EXECUTION_FLAGS), provisioner=worker,
                                               stdout=output, isolated=True))
                self.assertEqual(["create", "mint"], calls)
                self.assertTrue((worker.state / "attempt.json").is_file())
                self.assertTrue(json.loads(output.getvalue())["manualReconciliationRequired"])
                self.assertNotIn(TOKEN, output.getvalue())

    def test_rejected_mint_means_zero_secret_writes_and_restart_is_blocked(self):
        worker, calls, _ = self.prepared(fail_mint=True)
        self.reject("mint_failed", worker.execute)
        self.assertEqual(["create", "mint"], calls)
        marker = worker.state / "attempt.json"
        content = marker.read_bytes()
        restarted = self.worker()
        self.reject("prior_attempt_requires_manual_reconciliation", restarted.execute)
        restarted.command.assert_not_called()
        restarted.request.assert_not_called()
        self.assertEqual(content, marker.read_bytes())

    def test_ambiguous_create_has_no_retry_mint_or_write(self):
        worker, calls, _ = self.prepared(fail_create=True)
        self.reject("fixture_create_result_ambiguous", worker.execute)
        self.assertEqual(["create"], calls)
        self.assertTrue((worker.state / "attempt.json").exists())

    def test_ambiguous_secret_write_has_no_retry_and_requires_reconciliation(self):
        worker, calls, _ = self.prepared(fail_write=True)
        output = io.StringIO()
        self.assertEqual(1, subject.run(["script"] + list(subject.EXECUTION_FLAGS), provisioner=worker, stdout=output, isolated=True))
        self.assertEqual(["create", "mint", "write"], calls)
        self.assertTrue(json.loads(output.getvalue())["manualReconciliationRequired"])
        self.assertTrue((worker.state / "attempt.json").exists())

    def test_interlock_uses_exclusive_creation_and_preserves_first_marker(self):
        directory = self.root / "state"
        subject.create_interlock(directory, "first", HEAD)
        marker = directory / "attempt.json"
        content = marker.read_bytes()
        self.assertEqual(0o600, stat.S_IMODE(marker.stat().st_mode))
        self.assertEqual(0o700, stat.S_IMODE(directory.stat().st_mode))
        self.reject("prior_attempt_requires_manual_reconciliation", lambda: subject.create_interlock(directory, "second", HEAD))
        self.assertEqual(content, marker.read_bytes())

    def test_source_head_moving_before_mutation_prevents_creation(self):
        worker, calls, _ = self.prepared()
        worker.source_gate.side_effect = [HEAD, "b" * 40]
        self.reject("source_head_moved", worker.execute)
        self.assertEqual([], calls)
        self.assertFalse((worker.state / "attempt.json").exists())

    def test_provider_precondition_failure_never_creates_marker_or_fixture(self):
        worker, calls, _ = self.prepared()
        worker.provider_gate.side_effect = subject.Rejected("password_provider_not_enabled")
        self.reject("password_provider_not_enabled", worker.execute)
        self.assertEqual([], calls)
        self.assertFalse((worker.state / "attempt.json").exists())

    def test_create_transport_failure_is_not_retried(self):
        worker, calls, _ = self.prepared()
        worker.request = Mock(side_effect=TimeoutError("synthetic-provider-secret"))
        output = io.StringIO()
        self.assertEqual(1, subject.run(["script"] + list(subject.EXECUTION_FLAGS), provisioner=worker, stdout=output, isolated=True))
        self.assertEqual(1, worker.request.call_count)
        self.assertEqual([], calls)
        self.assertTrue((worker.state / "attempt.json").exists())
        self.assertNotIn("synthetic-provider-secret", output.getvalue())
        self.assertTrue(json.loads(output.getvalue())["manualReconciliationRequired"])

    def test_marker_io_failure_reports_manual_reconciliation(self):
        worker, calls, _ = self.prepared()
        output = io.StringIO()
        def fail_marker_sync(fd):
            if stat.S_ISREG(os.fstat(fd).st_mode):
                raise OSError("synthetic failure")
        with patch.object(subject.os, "fsync", side_effect=fail_marker_sync):
            self.assertEqual(1, subject.run(["script"] + list(subject.EXECUTION_FLAGS), provisioner=worker, stdout=output, isolated=True))
        self.assertEqual([], calls)
        self.assertTrue((worker.state / "attempt.json").exists())
        self.assertTrue(json.loads(output.getvalue())["manualReconciliationRequired"])

    def test_interlock_survives_post_open_io_failure(self):
        directory = self.root / "state"
        def fail_marker_sync(fd):
            if stat.S_ISREG(os.fstat(fd).st_mode):
                raise OSError("synthetic failure")
        with patch.object(subject.os, "fsync", side_effect=fail_marker_sync):
            with self.assertRaises(OSError):
                subject.create_interlock(directory, "first", HEAD)
        self.assertTrue((directory / "attempt.json").exists())
        self.reject("prior_attempt_requires_manual_reconciliation", lambda: subject.create_interlock(directory, "second", HEAD))


class UnsafeFilesystemTests(OfflineCase):
    def test_interlock_directory_rejects_mode_owner_and_symlink(self):
        directory = self.root / "unsafe"
        directory.mkdir(mode=0o755)
        self.reject("attempt_directory_unsafe", lambda: subject.create_interlock(directory, "attempt", HEAD))
        directory.chmod(0o700)
        with patch.object(subject.os, "geteuid", return_value=os.geteuid() + 1):
            self.reject("attempt_directory_unsafe", lambda: subject.create_interlock(directory, "attempt", HEAD))
        link = self.root / "symlink"
        link.symlink_to(directory, target_is_directory=True)
        self.reject("attempt_directory_unsafe", lambda: subject.create_interlock(link, "attempt", HEAD))
        self.assertFalse((directory / "attempt.json").exists())

    def test_interlock_marker_rejects_hardlink_owner_and_mode(self):
        real_open, real_fstat = os.open, os.fstat
        for unsafe in ("hardlink", "owner", "mode"):
            with self.subTest(unsafe=unsafe):
                directory = self.root / unsafe
                def opened(path, flags, mode=0o777):
                    fd = real_open(path, flags, mode)
                    if Path(path).name == "attempt.json" and unsafe == "hardlink":
                        os.link(path, self.root / "marker-hardlink")
                    return fd
                def inspected(fd):
                    info = real_fstat(fd)
                    if stat.S_ISREG(info.st_mode) and unsafe != "hardlink":
                        values = list(info)
                        if unsafe == "owner":
                            values[4] = os.geteuid() + 1
                        else:
                            values[0] = stat.S_IFREG | 0o644
                        return os.stat_result(values)
                    return info
                with patch.object(subject.os, "open", side_effect=opened), patch.object(subject.os, "fstat", side_effect=inspected):
                    self.reject("attempt_marker_unsafe", lambda: subject.create_interlock(directory, "attempt", HEAD))
                self.assertTrue((directory / "attempt.json").exists())
                self.assertEqual(b"", (directory / "attempt.json").read_bytes())

    def test_mint_input_rejects_hardlink_owner_and_mode_before_credentials_written(self):
        real_mkstemp, real_fstat = tempfile.mkstemp, os.fstat
        for unsafe in ("hardlink", "owner", "mode"):
            with self.subTest(unsafe=unsafe):
                paths = []
                worker = self.worker()
                def created(**kwargs):
                    fd, path = real_mkstemp(dir=self.root, **kwargs)
                    paths.append(Path(path))
                    if unsafe == "hardlink":
                        os.link(path, self.root / "input-hardlink")
                    return fd, path
                def inspected(fd):
                    info = real_fstat(fd)
                    if unsafe != "hardlink":
                        values = list(info)
                        if unsafe == "owner":
                            values[4] = os.geteuid() + 1
                        else:
                            values[0] = stat.S_IFREG | 0o644
                        return os.stat_result(values)
                    return info
                with patch.object(subject.tempfile, "mkstemp", side_effect=created), patch.object(subject.os, "fstat", side_effect=inspected):
                    self.reject("mint_input_file_unsafe", lambda: worker.mint({"expectedUid": "synthetic-uid", "password": "synthetic-password"}))
                worker.command.assert_not_called()
                self.assertEqual(1, len(paths))
                self.assertFalse(paths[0].exists())
                if unsafe == "hardlink":
                    self.assertEqual(b"", (self.root / "input-hardlink").read_bytes())


class MinterAndCommandTests(OfflineCase):
    def test_minter_runs_verified_memory_bytes_after_disk_changes(self):
        worker = self.worker()
        trusted = worker.minter_source
        (self.root / "scripts").mkdir()
        (self.root / "scripts/mint-development-firebase-diagnostic-probe.py").write_text("raise RuntimeError('untrusted')")
        worker.command = Mock(return_value=minter_output("synthetic-uid"))
        worker.mint({"expectedUid": "synthetic-uid"})
        args = worker.command.call_args.args[0]
        self.assertEqual(["/usr/bin/python3", "-I", "-B", "-c", trusted.decode()], args)
        worker.minter_source = b"untrusted source"
        worker.command.reset_mock()
        self.reject("minter_source_mismatch", lambda: worker.mint({"expectedUid": "synthetic-uid"}))
        worker.command.assert_not_called()

    def test_minter_unlinked_file_is_closed_after_exception(self):
        streams = []
        def command(args, **kwargs):
            stream = kwargs["stdin_file"]
            streams.append(stream)
            self.assertEqual(0, os.fstat(stream.fileno()).st_nlink)
            raise RuntimeError("synthetic failure")
        with self.assertRaises(RuntimeError):
            self.worker(command=command).mint({"expectedUid": "synthetic-uid", "password": "synthetic-password"})
        self.assertTrue(streams[0].closed)

    def test_bad_mint_evidence_is_rejected_before_use(self):
        for changes, reason in [({"tokenSha256Prefix": "incorrect"}, "mint_evidence_invalid"),
                                ({"expiresAt": NOW + 10}, "probe_lifetime_invalid"),
                                ({"expiresAt": NOW + 4000}, "probe_lifetime_invalid"),
                                ({"expiresAt": True}, "probe_lifetime_invalid")]:
            command = Mock(return_value=minter_output("synthetic-uid", **changes))
            self.reject(reason, lambda: self.worker(command=command).mint({"expectedUid": "synthetic-uid"}))
            self.assertEqual(1, command.call_count)

    def test_duplicate_json_fields_and_oversized_json_are_rejected(self):
        self.reject("duplicate_json_key", lambda: subject.decode(b'{"x":1,"x":2}'))
        with patch.object(subject, "MAX_BYTES", 32):
            self.reject("json_size_invalid", lambda: subject.decode(b" " * 33))

    def test_real_local_child_gets_no_inherited_credential_environment(self):
        program = "import json,os,sys; print(json.dumps(sorted(os.environ))); print(sys.stdin.read())"
        with patch.dict(os.environ, {"SYNTHETIC_CREDENTIAL": "synthetic-secret", "ANTHROPIC_API_KEY": "synthetic-secret"}):
            code, out, err = subject.bounded_command([sys.executable, "-I", "-c", program], data=b"synthetic-input")
        self.assertEqual(0, code)
        lines = out.decode().splitlines()
        self.assertNotIn("SYNTHETIC_CREDENTIAL", json.loads(lines[0]))
        self.assertNotIn("ANTHROPIC_API_KEY", json.loads(lines[0]))
        self.assertEqual("synthetic-input", lines[1])
        self.assertEqual(b"", err)

    def test_both_command_streams_are_capped(self):
        for stream in ("stdout", "stderr"):
            with self.subTest(stream=stream), patch.object(subject, "MAX_BYTES", 128):
                self.reject("command_output_too_large", lambda: subject.bounded_command(
                    [sys.executable, "-I", "-c", "import sys; sys." + stream + ".write('x'*1024)"]))

    def test_timeout_kills_and_reaps_the_local_child(self):
        real_popen = subject.subprocess.Popen
        children = []
        def popen(*args, **kwargs):
            child = real_popen(*args, **kwargs)
            children.append(child)
            return child
        with patch.object(subject.subprocess, "Popen", side_effect=popen), patch.object(subject.time, "monotonic", side_effect=[0, 31]):
            self.reject("command_timeout", lambda: subject.bounded_command(
                [sys.executable, "-I", "-c", "import time; time.sleep(30)"]))
        self.assertEqual(1, len(children))
        self.assertIsNotNone(children[0].poll())
        self.assertLess(children[0].returncode, 0)
        self.assertTrue(children[0].stdout.closed)
        self.assertTrue(children[0].stderr.closed)


if __name__ == "__main__":
    unittest.main()
