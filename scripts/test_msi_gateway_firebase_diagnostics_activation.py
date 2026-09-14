import base64
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import call, patch


SCRIPT = Path(__file__).with_name("jeeb-msi-gateway-firebase-diagnostics-admin.py")
spec = importlib.util.spec_from_file_location("subject", SCRIPT)
subject = importlib.util.module_from_spec(spec)
spec.loader.exec_module(subject)


def probe(**changes):
    value = {
        "idToken": "header.payload.signature",
        "expectedSubject": "firebase-uid",
        "expectedProvider": "google.com",
    }
    value.update(changes)
    return json.dumps(value, separators=(",", ":")).encode()


def artifact(path, commit="a" * 40, tree="b" * 40, extra=None):
    manifest = json.dumps(
        {
            "schema": 1,
            "repository": subject.REPOSITORY,
            "commitSha": commit,
            "sourceTree": tree,
            "runtime": subject.RUNTIME,
        },
        separators=(",", ":"),
    ).encode()
    entries = {
        "manifest.json": manifest,
        "payload/JeebGateway": b"entrypoint",
        "payload/JeebGateway.dll": b"dll",
        "payload/JeebGateway.runtimeconfig.json": b"{}",
    }
    entries.update(extra or {})
    with tarfile.open(path, "w:gz") as bundle:
        for name, raw in entries.items():
            item = tarfile.TarInfo(name)
            item.size = len(raw)
            item.uid = 0
            item.gid = 0
            item.mode = 0o755 if name == "payload/JeebGateway" else 0o644
            bundle.addfile(item, io.BytesIO(raw))


class MsiGatewayFirebaseDiagnosticsActivationTests(unittest.TestCase):
    def test_fixed_host_service_paths_and_shared_lock(self):
        self.assertEqual(subject.HOST, "ouday-GT70-2OC-2OD")
        self.assertEqual(subject.UNIT, "jeeb-gateway.service")
        self.assertEqual(subject.PORT, 10090)
        self.assertEqual(
            subject.INSTALL_PATH,
            Path("/usr/local/sbin/jeeb-msi-gateway-firebase-diagnostics-admin"),
        )
        self.assertEqual(subject.LOCK_PATH, Path("/run/jeeb-msi-service-deploy.lock"))
        self.assertEqual(
            subject.RELEASE_ROOT,
            Path("/opt/jeeb-gateway-releases/firebase-diagnostics"),
        )
        self.assertTrue(SCRIPT.read_text().startswith("#!/usr/bin/python3 -I\n"))

    def test_predecessor_identity_is_fully_pinned_without_secret_values(self):
        source = SCRIPT.read_text()
        self.assertEqual(
            subject.PREDECESSOR,
            Path(
                "/home/ec2-user/jeeb-native-builds/20260910/"
                "jeeb-gateway-686b6e08-linux-x64"
            ),
        )
        self.assertEqual(len(subject.EXPECTED_DROPINS), 8)
        self.assertEqual(len(subject.EXPECTED_SHA256), 4)
        self.assertTrue(all(len(value) == 64 for value in subject.EXPECTED_SHA256.values()))
        self.assertEqual(len(subject.EXPECTED_PREDECESSOR_TREE_IDENTITY), 64)
        self.assertEqual(len(subject.EXPECTED_DROPIN_IDENTITY), 64)
        self.assertNotIn("PENDING", source)
        self.assertEqual(
            (
                subject.EXPECTED_PREDECESSOR_TREE_DIRECTORIES,
                subject.EXPECTED_PREDECESSOR_TREE_FILES,
            ),
            (6, 409),
        )
        self.assertNotIn("FIREBASE_CONFIG_PATH", source)
        self.assertNotIn("PRIVATE KEY", source)

    def test_canonical_tree_identity_covers_every_path_metadata_and_content(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "runtime"
            (root / "nested").mkdir(parents=True)
            first = root / "JeebGateway.dll"
            second = root / "nested" / "dependency.dll"
            first.write_bytes(b"one")
            second.write_bytes(b"two")
            with patch.object(subject, "extended_attribute_names", return_value=[]):
                digest, directories, files = subject.tree_identity(root)
            self.assertEqual((directories, files), (2, 2))
            self.assertEqual(len(digest), 64)

            second.write_bytes(b"changed")
            with patch.object(subject, "extended_attribute_names", return_value=[]):
                changed, _, _ = subject.tree_identity(root)
            self.assertNotEqual(changed, digest)
            second.write_bytes(b"two")
            second.chmod(0o600)
            with patch.object(subject, "extended_attribute_names", return_value=[]):
                metadata_changed, _, _ = subject.tree_identity(root)
            self.assertNotEqual(metadata_changed, digest)

            with patch.object(
                subject,
                "extended_attribute_names",
                return_value=["security.capability"],
            ), self.assertRaisesRegex(
                subject.Rejected, "manifest_extended_attributes_present"
            ):
                subject.tree_identity(root)

    def test_probe_contract_is_exact_and_bounded(self):
        self.assertEqual(subject.validate_probe(probe())["expectedProvider"], "google.com")
        self.assertEqual(
            len(subject.validate_probe(probe(expectedSubject="x" * 128))["expectedSubject"]),
            128,
        )
        for raw, reason in [
            (probe(expectedSubject="x" * 129), "probe_contract_invalid"),
            (probe(idToken=""), "probe_contract_invalid"),
            (probe(expectedProvider="google com"), "probe_contract_invalid"),
            (probe(extra="no"), "probe_contract_invalid"),
            (b"{}", "probe_contract_invalid"),
        ]:
            with self.subTest(reason=reason), self.assertRaisesRegex(subject.Rejected, reason):
                subject.validate_probe(raw)

    def test_duplicate_probe_key_is_rejected(self):
        raw = probe()[:-1] + b',"expectedSubject":"other"}'
        with self.assertRaisesRegex(subject.Rejected, "probe_duplicate_key"):
            subject.validate_probe(raw)

    def test_success_and_invalid_probes_cover_both_route_twins(self):
        verified = {
            "verified": True,
            "projectId": subject.PROJECT,
            "provider": "google.com",
            "subjectSha256": hashlib.sha256(b"firebase-uid").hexdigest(),
        }
        with patch.object(
            subject,
            "health_request",
            side_effect=[
                (200, json.dumps(verified).encode()),
                (200, json.dumps(verified).encode()),
            ],
        ) as requested:
            subject.successful_diagnostic_probe(subject.validate_probe(probe()))
        self.assertEqual(
            [item.args[0] for item in requested.call_args_list],
            [
                "/v1/auth/diagnostics/firebase-token",
                "/auth/diagnostics/firebase-token",
            ],
        )

        with patch.object(
            subject,
            "health_request",
            side_effect=[
                (401, b'{"verified":false}'),
                (401, b'{"verified":false}'),
            ],
        ) as requested:
            subject.diagnostic_probe()
        self.assertEqual(
            [item.args[0] for item in requested.call_args_list],
            [
                "/v1/auth/diagnostics/firebase-token",
                "/auth/diagnostics/firebase-token",
            ],
        )

    def test_dropin_changes_only_runtime_and_exact_diagnostic_configuration(self):
        target = subject.RELEASE_ROOT / ("jeeb-gateway-" + "a" * 40 + "-linux-x64")
        self.assertEqual(
            subject.dropin_text(target),
            "[Service]\n"
            f"WorkingDirectory={target}\n"
            "ExecStart=\n"
            "ExecStart=/bin/bash -c 'set -a; source "
            "/opt/jeeb-gateway-releases/firebase-diagnostics/"
            "predecessor-686b6e08-linux-x64/gateway.env; set +a; exec ./JeebGateway'\n"
            "Environment=Auth__FirebaseTokenDiagnostics__Enabled=true\n"
            "Environment=Auth__FirebaseTokenDiagnostics__Environment=development\n"
            "Environment=Auth__FirebaseTokenDiagnostics__ProjectId=jeeb-development-msi\n",
        )
        self.assertNotIn("ASPNETCORE_ENVIRONMENT", subject.dropin_text(target))
        self.assertIn("Auth__FirebaseTokenDiagnostics__Enabled=false", subject.rollback_dropin_text())
        self.assertIn(str(subject.ROLLBACK_RUNTIME), subject.rollback_dropin_text())

    def test_manifest_accepts_only_exact_repository_runtime_and_required_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / "candidate.tar.gz"
            artifact(archive)
            manifest = subject.manifest_from_archive(archive)
            self.assertEqual(manifest["commitSha"], "a" * 40)
            self.assertEqual(manifest["sourceTree"], "b" * 40)

            artifact(archive, commit="not-a-sha")
            with self.assertRaisesRegex(subject.Rejected, "artifact_manifest_invalid"):
                subject.manifest_from_archive(archive)

    def test_archive_rejects_traversal(self):
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / "candidate.tar.gz"
            artifact(archive, extra={"payload/../escape": b"bad"})
            with self.assertRaisesRegex(subject.Rejected, "artifact_path_invalid"):
                subject.manifest_from_archive(archive)

    def test_signed_envelope_rejects_unsigned_artifact_before_manifest_use(self):
        with tempfile.TemporaryDirectory() as temporary, patch.multiple(
            subject,
            STATE_DIRECTORY=Path(temporary),
            STAGED_ARCHIVE=Path(temporary) / "candidate.tar.gz",
        ):
            with self.assertRaisesRegex(subject.Rejected, "artifact_envelope_invalid"):
                subject.read_signed_artifact(io.BytesIO(b"not-an-envelope"))

    def test_signed_envelope_accepts_only_the_pinned_key_and_algorithm(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "source.tar.gz"
            artifact(archive)
            private_key = root / "private.pem"
            public_key = root / "public.pem"
            signature = root / "signature"
            subprocess.run(
                ["/usr/bin/openssl", "genrsa", "-out", private_key, "3072"],
                check=True,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            subprocess.run(
                ["/usr/bin/openssl", "rsa", "-in", private_key, "-pubout", "-out", public_key],
                check=True,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            subprocess.run(
                ["/usr/bin/openssl", "dgst", "-sha256", "-sign", private_key, "-out", signature],
                input=(
                    subject.ARTIFACT_MAGIC
                    + b"c" * 64
                    + b"\n"
                    + b"binding\n"
                    + archive.read_bytes()
                ),
                check=True,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            envelope = io.BytesIO(
                subject.ARTIFACT_MAGIC
                + b"c" * 64
                + b"\n"
                + b"binding\n"
                + base64.b64encode(signature.read_bytes())
                + b"\n"
                + archive.read_bytes()
            )
            staged = root / "candidate.tar.gz"

            def write_archive(source):
                raw = source.read()
                staged.write_bytes(raw)
                return hashlib.sha256(raw).hexdigest()

            with patch.multiple(
                subject,
                STATE_DIRECTORY=root,
                STAGED_ARCHIVE=staged,
                SIGNING_PUBLIC_KEY=public_key.read_bytes(),
            ), patch.object(subject, "write_staged_archive", side_effect=write_archive), patch.object(
                subject, "require_challenge"
            ) as challenged:
                digest = subject.read_signed_artifact(envelope)
            self.assertEqual(digest, hashlib.sha256(archive.read_bytes()).hexdigest())
            challenged.assert_called_once_with("c" * 64, "binding")

    def test_committed_public_key_fingerprint_matches_magic_and_runbook(self):
        result = subprocess.run(
            ["/usr/bin/openssl", "rsa", "-pubin", "-outform", "DER"],
            input=subject.SIGNING_PUBLIC_KEY,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            check=True,
        )
        fingerprint = hashlib.sha256(result.stdout).hexdigest()
        self.assertEqual(fingerprint, subject.SIGNING_PUBLIC_KEY_SHA256)
        self.assertIn(fingerprint.encode(), subject.ARTIFACT_MAGIC)

    def test_candidate_preflight_requires_all_effective_diagnostic_values(self):
        source = SCRIPT.read_text()
        for exact in [
            'process_environment(pid, "Auth__FirebaseTokenDiagnostics__Enabled") == "true"',
            'process_environment(pid, "Auth__FirebaseTokenDiagnostics__Environment")',
            'process_environment(pid, "Auth__FirebaseTokenDiagnostics__ProjectId") == PROJECT',
        ]:
            self.assertIn(exact, source)
        self.assertIn('process_environment(pid, "ASPNETCORE_ENVIRONMENT") == "Production"', source)
        self.assertIn('"predecessor_diagnostic_binding_present"', source)

    def test_operation_lock_requires_single_link_root_owned_nonblocking_file(self):
        run = type(
            "Stat",
            (),
            {"st_mode": stat.S_IFDIR | 0o755, "st_uid": 0, "st_gid": 0},
        )()
        lock = type(
            "Stat",
            (),
            {
                "st_mode": stat.S_IFREG | 0o600,
                "st_uid": 0,
                "st_gid": 0,
                "st_nlink": 1,
            },
        )()
        with patch.object(Path, "lstat", autospec=True, return_value=run), patch.object(
            subject.os, "open", return_value=44
        ) as opened, patch.object(subject.os, "fstat", return_value=lock), patch.object(
            subject.fcntl, "flock"
        ) as flocked, patch.object(subject.os, "close") as closed:
            with subject.operation_lock():
                pass
        expected_flags = (
            os.O_RDWR
            | os.O_CREAT
            | os.O_NONBLOCK
            | getattr(os, "O_NOFOLLOW", 0)
            | getattr(os, "O_CLOEXEC", 0)
        )
        opened.assert_called_once_with(subject.LOCK_PATH, expected_flags, 0o600)
        flocked.assert_called_once_with(44, subject.fcntl.LOCK_EX | subject.fcntl.LOCK_NB)
        closed.assert_called_once_with(44)

    def test_helper_ignores_session_termination_until_transaction_finishes(self):
        with patch.object(subject.signal, "signal") as configured:
            subject.ignore_session_termination()
        configured.assert_has_calls(
            [
                call(subject.signal.SIGHUP, subject.signal.SIG_IGN),
                call(subject.signal.SIGINT, subject.signal.SIG_IGN),
                call(subject.signal.SIGTERM, subject.signal.SIG_IGN),
                call(subject.signal.SIGQUIT, subject.signal.SIG_IGN),
            ]
        )

    def test_candidate_readiness_keeps_roster_and_rejects_new_regressions(self):
        baseline = {
            "self": "Healthy",
            **{name: "Degraded" for name in subject.EXPECTED_FAILING},
        }

        def response(statuses):
            failing = [name for name, state in statuses.items() if state != "Healthy"]
            return json.dumps(
                {
                    "status": "Degraded" if failing else "Healthy",
                    "checks": [
                        {"name": name, "status": state}
                        for name, state in statuses.items()
                    ],
                    "failing": failing,
                }
            ).encode()

        improved = dict(baseline)
        improved[next(iter(subject.EXPECTED_FAILING))] = "Healthy"
        with patch.object(
            subject,
            "health_request",
            side_effect=[(200, b""), (200, response(improved))],
        ):
            self.assertEqual(subject.readiness_snapshot(baseline), improved)

        regressed = dict(baseline)
        regressed["self"] = "Degraded"
        with patch.object(
            subject,
            "health_request",
            side_effect=[(200, b""), (200, response(regressed))],
        ), self.assertRaisesRegex(subject.Rejected, "readiness_regressed"):
            subject.readiness_snapshot(baseline)

    def test_failed_candidate_readiness_restarts_only_gateway_and_rolls_back(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            dropin = root / "diagnostics.conf"
            target = root / "candidate"
            target.mkdir()
            archive = root / "candidate.tar.gz"
            archive.write_bytes(b"archive")
            receipt = {
                "commitSha": "a" * 40,
                "archiveSha256": "b" * 64,
                "readinessBaseline": {"self": "Healthy", **{name: "Degraded" for name in subject.EXPECTED_FAILING}},
            }

            def fake_write(_target):
                dropin.write_text("candidate")

            with patch.multiple(
                subject,
                DROPIN=dropin,
                STAGED_ARCHIVE=archive,
                STAGED_RECEIPT=root / "candidate.json",
            ), patch.object(subject, "host_preflight") as preflight, patch.object(
                subject, "load_receipt", return_value=(receipt, target)
            ), patch.object(
                subject, "stopped_predecessor_preflight"
            ), patch.object(
                subject, "ensure_rollback_snapshot"
            ), patch.object(
                subject, "verify_rollback_snapshot"
            ), patch.object(
                subject, "write_dropin", side_effect=fake_write
            ), patch.object(
                subject,
                "replace_with_rollback_dropin",
                side_effect=lambda _target: dropin.write_text("rollback"),
            ), patch.object(
                subject, "stopped_rollback_preflight"
            ), patch.object(
                subject, "rollback_host_preflight"
            ), patch.object(
                subject,
                "wait_readiness",
                side_effect=[subject.Rejected("candidate_unhealthy"), None],
            ), patch.object(subject, "systemctl") as systemctl, patch.object(
                subject, "remove_staged"
            ) as remove_staged, self.assertRaisesRegex(
                subject.Rejected, "activation_failed_rolled_back"
            ):
                subject.activate(probe())

            self.assertEqual(
                systemctl.call_args_list,
                [
                    call("stop", "jeeb-gateway.service"),
                    call("daemon-reload"),
                    call("restart", "jeeb-gateway.service"),
                    call("stop", "jeeb-gateway.service"),
                    call("daemon-reload"),
                    call("restart", "jeeb-gateway.service"),
                ],
            )
            self.assertEqual(dropin.read_text(), "rollback")
            remove_staged.assert_called_once_with(target)
            self.assertEqual(preflight.call_args_list, [call()])

    def test_main_buffers_stdin_before_ignoring_signals_or_taking_lock(self):
        source = SCRIPT.read_text()
        main = source[source.index("def main():") :]
        self.assertLess(main.index("buffer_input(sys.stdin.buffer"), main.index("ignore_session_termination()"))
        self.assertLess(main.index("ignore_session_termination()"), main.index("with operation_lock():"))

    def test_activation_snapshots_after_stop_and_before_candidate_publication(self):
        source = SCRIPT.read_text()
        activation = source[source.index("def activate(probe_raw):") : source.index("def source_sha256():")]
        self.assertLess(activation.index('systemctl("stop", UNIT)'), activation.index("stopped_predecessor_preflight()"))
        self.assertLess(activation.index("stopped_predecessor_preflight()"), activation.index("ensure_rollback_snapshot()"))
        self.assertLess(activation.index("ensure_rollback_snapshot()"), activation.index("write_dropin(target)"))
        self.assertIn("replace_with_rollback_dropin(target)", activation)
        self.assertIn("stopped_rollback_preflight()", activation)
        self.assertIn("rollback_host_preflight(receipt", activation)

        snapshot = source[source.index("def ensure_rollback_snapshot():") : source.index("def write_json_exclusive")]
        self.assertLess(snapshot.index("tempfile.mkdtemp"), snapshot.index("verify_rollback_snapshot(temporary_directory)"))
        self.assertLess(snapshot.index("verify_rollback_snapshot(temporary_directory)"), snapshot.index("os.rename(temporary_directory, ROLLBACK_DIRECTORY)"))

    def test_workflow_accepts_verified_predecessor_or_rollback_retry_state(self):
        workflow = SCRIPT.parent.parent / ".github/workflows/jeeb-msi-gateway-firebase-diagnostics-activate.yml"
        text = workflow.read_text()
        self.assertEqual(
            text.count('assert value["runtime"] in {"predecessor", "rollback"}'),
            2,
        )
        self.assertIn('assert value["runtime"] == "candidate"', text)

    def test_workflow_is_manual_protected_signed_and_uses_exact_commands(self):
        workflow = SCRIPT.parent.parent / ".github/workflows/jeeb-msi-gateway-firebase-diagnostics-activate.yml"
        text = workflow.read_text()
        self.assertIn("workflow_dispatch:", text)
        self.assertIn("github.ref == 'refs/heads/main'", text)
        self.assertIn("github.ref_protected", text)
        self.assertIn("environment: development-msi-gateway-signing", text)
        self.assertNotIn("environment: development\n", text)
        self.assertIn("branches/main --jq '.commit.sha'", text)
        self.assertIn("JEEB_MSI_GATEWAY_ARTIFACT_SIGNING_KEY_PEM", text)
        self.assertIn("JEEB_DEVELOPMENT_FIREBASE_DIAGNOSTIC_PROBE_JSON", text)
        self.assertIn(
            "CLOUDFLARED_SHA256: fcfb02b575a52ca1af2e3267af4e1517bcdeb30ac48c8340c69abaed3c0576ad2",
            text,
        )
        self.assertIn("openssl dgst -sha256 -sign", text)
        self.assertEqual(text.count("printf '%s\\n' \"$NONCE\""), 2)
        self.assertLess(
            text.index('unset ARTIFACT_SIGNING_KEY'),
            text.index('openssl dgst -sha256 -sign'),
        )
        self.assertLess(
            text.index('unset DIAGNOSTIC_PROBE'),
            text.index('cat "$probe_file" | setsid --wait ssh'),
        )
        self.assertIn("/usr/local/sbin/jeeb-msi-gateway-firebase-diagnostics-admin preflight", text)
        self.assertIn("/usr/local/sbin/jeeb-msi-gateway-firebase-diagnostics-admin stage", text)
        self.assertIn("/usr/local/sbin/jeeb-msi-gateway-firebase-diagnostics-admin activate", text)
        self.assertIn("--runtime linux-x64 --self-contained true", text)
        self.assertNotIn("repository_dispatch", text)
        self.assertNotIn("schedule:", text)
        self.assertNotIn("jeeb-user-management.service", text)

    def test_helper_never_targets_user_management_or_prints_request_material(self):
        text = SCRIPT.read_text()
        self.assertNotIn("jeeb-user-management.service", text)
        self.assertNotIn("journalctl", text)
        self.assertNotIn("stderr=subprocess.PIPE", text)
        self.assertEqual(text.count('systemctl("restart", UNIT)'), 3)
        self.assertNotIn('systemctl("restart", "', text)

    def test_runbook_pins_install_path_commands_and_configuration(self):
        runbook = SCRIPT.parent.parent / "docs/runbooks/msi-gateway-firebase-diagnostics.md"
        text = runbook.read_text()
        self.assertIn("/usr/bin/install -o root -g root -m 0755", text)
        self.assertIn("/run/jeeb-msi-service-deploy.lock", text)
        self.assertIn("Auth__FirebaseTokenDiagnostics__Enabled=true", text)
        self.assertIn("Auth__FirebaseTokenDiagnostics__Environment=development", text)
        self.assertIn("Auth__FirebaseTokenDiagnostics__ProjectId=jeeb-development-msi", text)
        self.assertIn(subject.SIGNING_PUBLIC_KEY_SHA256, text)


if __name__ == "__main__":
    unittest.main()
