import base64
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import stat
import unittest

SCRIPT = Path(__file__).with_name("mint-development-firebase-diagnostic-probe.py")
HELPER = Path(__file__).with_name("jeeb-msi-gateway-firebase-diagnostics-admin.py")
RUNBOOK = SCRIPT.parent.parent / "docs/runbooks/msi-gateway-firebase-diagnostics.md"
CI = SCRIPT.parent.parent / ".github/workflows/ci.yml"


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


subject = load("mint_subject", SCRIPT)
helper = load("helper_subject", HELPER)

NOW = 1_800_000_000
UID = "fixtureUid0123456789abcdefgh"
# Fixture values are concatenated so no single literal resembles a credential.
FIXTURE_KEY = "fixture-web-api-" + "key-0123456789ab"
FIXTURE_EMAIL = "probe@example.invalid"
FIXTURE_PASSWORD = "fixture-" + "password-value"


def b64url(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode()


def token(**changes):
    header = {"alg": "RS256", "kid": "fixture-kid", "typ": "JWT"}
    claims = {
        "iss": subject.ISSUER,
        "aud": subject.PROJECT,
        "auth_time": NOW - 5,
        "user_id": UID,
        "sub": UID,
        "iat": NOW - 5,
        "exp": NOW - 5 + 3600,
        "firebase": {"identities": {}, "sign_in_provider": "password"},
    }
    header.update(changes.pop("header", {}))
    signature = changes.pop("signature", b64url(b"fixture-signature-bytes"))
    claims.update(changes)
    return ".".join(
        (
            b64url(json.dumps(header, separators=(",", ":")).encode()),
            b64url(json.dumps(claims, separators=(",", ":")).encode()),
            signature,
        )
    )


def request(**changes):
    value = {
        "webApiKey": FIXTURE_KEY,
        "email": FIXTURE_EMAIL,
        "password": FIXTURE_PASSWORD,
        "expectedUid": UID,
    }
    value.update(changes)
    for key in [key for key, item in value.items() if item is None]:
        del value[key]
    return json.dumps(value, separators=(",", ":")).encode()


def response(**changes):
    value = {
        "kind": "identitytoolkit#VerifyPasswordResponse",
        "localId": UID,
        "email": FIXTURE_EMAIL,
        "displayName": "",
        "idToken": token(),
        "registered": True,
        "refreshToken": "fixture-refresh-" + "token-must-be-discarded",
        "expiresIn": "3600",
    }
    value.update(changes)
    return json.dumps(value).encode()


class Recorder:
    def __init__(self, status=200, body=None, error=None):
        self.status = status
        self.body = response() if body is None else body
        self.error = error
        self.calls = []

    def __call__(self, path, headers, body):
        self.calls.append((path, dict(headers), bytes(body)))
        if self.error is not None:
            raise self.error
        return self.status, self.body


def stat_result(mode, uid=0, size=64):
    return os.stat_result((mode, 1, 1, 1, uid, uid, size, 0, 0, 0))


class Stream:
    """Minimal stand-in for sys.stdin/sys.stdout with a fixed descriptor number."""

    def __init__(self, fd, data=b""):
        self.fd = fd
        self.buffer = io.BytesIO(data)

    def fileno(self):
        return self.fd


def process(raw=None, argv=("mint",), stdin_mode=stat.S_IFREG | 0o600, stdout_mode=stat.S_IFIFO | 0o600,
            stdin_uid=42, euid=42, isolated=True, minter=None):
    stdin = Stream(0, request() if raw is None else raw)
    stdout = Stream(1)
    stderr = io.StringIO()
    modes = {0: stat_result(stdin_mode, uid=stdin_uid, size=len(stdin.buffer.getvalue()) or 1), 1: stat_result(stdout_mode)}
    calls = []

    def fake_mint(value):
        calls.append(value)
        return subject.mint(value, post=Recorder(), now=lambda: NOW)

    code = subject.run(
        list(argv), stdin, stdout, stderr,
        fstat=lambda fd: modes[fd], euid=lambda: euid, isolated=isolated,
        minter=minter or fake_mint,
    )
    return code, stdin, stdout, stderr.getvalue(), calls


class MintDevelopmentFirebaseDiagnosticProbeTests(unittest.TestCase):
    def test_probe_matches_helper_contract_and_discards_refresh_token(self):
        post = Recorder()
        probe, evidence = subject.mint(request(), post=post, now=lambda: NOW)
        self.assertTrue(probe.endswith(b"\n"))
        value = json.loads(probe)
        self.assertEqual(tuple(value), subject.PROBE_FIELDS)
        self.assertEqual(value["expectedSubject"], UID)
        self.assertEqual(value["expectedProvider"], "password")
        self.assertEqual(value["idToken"], token())
        self.assertEqual(helper.validate_probe(probe), value)
        self.assertNotIn(b"refresh", probe)
        self.assertEqual(evidence["status"], "probe_minted")
        self.assertEqual(set(evidence), subject.EVIDENCE_FIELDS)

    def test_sign_in_request_is_fixed_and_keeps_key_out_of_the_path(self):
        post = Recorder()
        subject.mint(request(), post=post, now=lambda: NOW)
        self.assertEqual(len(post.calls), 1)
        path, headers, body = post.calls[0]
        self.assertEqual(path, "/v1/accounts:signInWithPassword")
        self.assertNotIn("key=", path)
        self.assertEqual(headers[subject.API_KEY_HEADER], FIXTURE_KEY)
        self.assertEqual(headers["Content-Type"], "application/json")
        self.assertEqual(
            json.loads(body),
            {"email": FIXTURE_EMAIL, "password": FIXTURE_PASSWORD, "returnSecureToken": True},
        )

    def test_evidence_contains_only_bounded_non_secret_values(self):
        _, evidence = subject.mint(request(), post=Recorder(), now=lambda: NOW)
        text = json.dumps(evidence)
        for secret in (FIXTURE_KEY, FIXTURE_EMAIL, FIXTURE_PASSWORD, UID, token(), "fixture-refresh"):
            self.assertNotIn(secret, text)
        self.assertEqual(evidence["uidSha256Prefix"], hashlib.sha256(UID.encode()).hexdigest()[:16])
        self.assertEqual(evidence["tokenSha256Prefix"], hashlib.sha256(token().encode()).hexdigest()[:16])
        self.assertTrue(re.fullmatch(r"[0-9a-f]{16}", evidence["uidSha256Prefix"]))
        self.assertEqual(evidence["expiresAt"], NOW - 5 + 3600)
        self.assertEqual(evidence["project"], "jeeb-development-msi")
        self.assertEqual(evidence["provider"], "password")

    def test_input_contract_is_exact_and_bounded(self):
        cases = {
            "input_contract_invalid": [
                request(expectedUid=None),
                request(extra="x"),
                request(password=""),
                request(webApiKey=7),
            ],
            "duplicate_key": [b'{"webApiKey":"a","webApiKey":"b"}'],
            "input_json_invalid": [b"{not json"],
            "input_size_invalid": [b"", b"{" + b" " * subject.MAX_INPUT_BYTES + b"}"],
            "web_api_key_invalid": [request(webApiKey="short"), request(webApiKey="has space " + "x" * 20)],
            "email_invalid": [request(email="no-at-sign"), request(email="a@b")],
            "expected_uid_invalid": [request(expectedUid="uid with space"), request(expectedUid="x" * 129)],
            "password_invalid": [request(password="p" * 1025)],
        }
        for reason, inputs in cases.items():
            for index, raw in enumerate(inputs):
                with self.subTest(reason=reason, case=index):
                    post = Recorder()
                    with self.assertRaises(subject.Rejected) as caught:
                        subject.mint(raw, post=post, now=lambda: NOW)
                    self.assertEqual(caught.exception.args[0], reason)
                    self.assertEqual(post.calls, [])

    def test_provider_response_binding_rejects_every_mismatch(self):
        cases = [
            ("sign_in_unreachable", Recorder(error=OSError("fixture network failure"))),
            ("sign_in_unreachable", Recorder(error=TimeoutError())),
            ("sign_in_http_400", Recorder(status=400, body=b'{"error":{"message":"fixture"}}')),
            ("sign_in_http_503", Recorder(status=503, body=b"")),
            ("response_too_large", Recorder(body=b" " * (subject.MAX_RESPONSE_BYTES + 1))),
            ("response_json_invalid", Recorder(body=b"{broken")),
            ("duplicate_key", Recorder(body=b'{"localId":"a","localId":"b"}')),
            ("response_shape_invalid", Recorder(body=b"[]")),
            ("identity_mismatch", Recorder(body=response(localId="someone-else"))),
            ("identity_not_registered", Recorder(body=response(registered=False))),
            ("id_token_invalid", Recorder(body=response(idToken=""))),
            ("id_token_invalid", Recorder(body=response(idToken="x" * (subject.MAX_ID_TOKEN_BYTES + 1)))),
            ("expires_in_invalid", Recorder(body=response(expiresIn=3600))),
            ("expires_in_invalid", Recorder(body=response(expiresIn="86400"))),
            ("id_token_shape_invalid", Recorder(body=response(idToken="only.two"))),
            ("id_token_shape_invalid", Recorder(body=response(idToken="invalid.jwt.diagnostic-probe"))),
            ("id_token_shape_invalid", Recorder(body=response(idToken=token(signature="not*base64url")))),
            ("id_token_header_invalid", Recorder(body=response(idToken=token(header={"alg": "HS256"})))),
            ("id_token_header_invalid", Recorder(body=response(idToken=token(header={"kid": ""})))),
            ("audience_mismatch", Recorder(body=response(idToken=token(aud="jeeb-5a293")))),
            ("issuer_mismatch", Recorder(body=response(idToken=token(iss="https://securetoken.google.com/jeeb-5a293")))),
            ("subject_mismatch", Recorder(body=response(idToken=token(sub="someone-else")))),
            ("subject_mismatch", Recorder(body=response(idToken=token(user_id="someone-else")))),
            ("provider_mismatch", Recorder(body=response(idToken=token(firebase={"sign_in_provider": "custom"})))),
            ("lifetime_invalid", Recorder(body=response(idToken=token(exp=NOW - 1)))),
            ("lifetime_invalid", Recorder(body=response(idToken=token(exp=NOW - 5 + 3601)))),
            ("lifetime_invalid", Recorder(body=response(idToken=token(iat=NOW + 61, exp=NOW + 3600)))),
            ("lifetime_invalid", Recorder(body=response(idToken=token(exp="3600")))),
            ("lifetime_too_short", Recorder(body=response(idToken=token(iat=NOW - 1801, exp=NOW + 1799)))),
        ]
        for reason, post in cases:
            with self.subTest(reason=reason):
                with self.assertRaises(subject.Rejected) as caught:
                    subject.mint(request(), post=post, now=lambda: NOW)
                self.assertEqual(caught.exception.args[0], reason)
                self.assertEqual(len(post.calls), 1)

    def test_stdin_and_stdout_guards(self):
        subject.require_private_stdin(stat_result(stat.S_IFREG | 0o600, uid=42), 42)
        subject.require_piped_stdout(stat_result(stat.S_IFIFO | 0o600))
        guards = [
            ("stdin_must_be_regular_file", lambda: subject.require_private_stdin(stat_result(stat.S_IFIFO | 0o600, uid=42), 42)),
            ("stdin_mode_must_be_0600", lambda: subject.require_private_stdin(stat_result(stat.S_IFREG | 0o640, uid=42), 42)),
            ("stdin_mode_must_be_0600", lambda: subject.require_private_stdin(stat_result(stat.S_IFREG | 0o400, uid=42), 42)),
            ("stdin_owner_invalid", lambda: subject.require_private_stdin(stat_result(stat.S_IFREG | 0o600, uid=41), 42)),
            ("input_size_invalid", lambda: subject.require_private_stdin(stat_result(stat.S_IFREG | 0o600, uid=42, size=0), 42)),
            ("input_size_invalid", lambda: subject.require_private_stdin(stat_result(stat.S_IFREG | 0o600, uid=42, size=subject.MAX_INPUT_BYTES + 1), 42)),
            ("stdout_must_be_pipe", lambda: subject.require_piped_stdout(stat_result(stat.S_IFREG | 0o600))),
            ("stdout_must_be_pipe", lambda: subject.require_piped_stdout(stat_result(stat.S_IFCHR | 0o600))),
            ("stdout_must_be_pipe", lambda: subject.require_piped_stdout(stat_result(stat.S_IFSOCK | 0o600))),
        ]
        for reason, guard in guards:
            with self.subTest(reason=reason):
                with self.assertRaises(subject.Rejected) as caught:
                    guard()
                self.assertEqual(caught.exception.args[0], reason)

    def test_run_enforces_guard_order_before_reading_input(self):
        code, stdin, stdout, stderr, calls = process()
        self.assertEqual(code, 0)
        self.assertEqual(calls, [request()])
        self.assertEqual(helper.validate_probe(stdout.buffer.getvalue())["expectedSubject"], UID)
        self.assertEqual(json.loads(stderr)["status"], "probe_minted")

        rejected = [
            ("isolated_mode_required", dict(isolated=False)),
            ("arguments_not_accepted", dict(argv=("mint", "extra"))),
            ("stdin_mode_must_be_0600", dict(stdin_mode=stat.S_IFREG | 0o644)),
            ("stdin_owner_invalid", dict(stdin_uid=7)),
            ("stdout_must_be_pipe", dict(stdout_mode=stat.S_IFREG | 0o600)),
        ]
        for reason, changes in rejected:
            with self.subTest(reason=reason):
                code, stdin, stdout, stderr, calls = process(**changes)
                self.assertEqual(code, 1)
                self.assertEqual(json.loads(stderr), {"status": "probe_rejected", "reason": reason})
                self.assertEqual(stdin.buffer.tell(), 0, "input must not be read before every guard passes")
                self.assertEqual(stdout.buffer.getvalue(), b"")
                self.assertEqual(calls, [])

        def exploding(_):
            raise RuntimeError("fixture detail that must never be printed")

        code, _, stdout, stderr, _ = process(minter=exploding)
        self.assertEqual(code, 1)
        self.assertEqual(json.loads(stderr), {"status": "probe_rejected", "reason": "execution_failed"})
        self.assertNotIn("fixture detail", stderr)
        self.assertEqual(stdout.buffer.getvalue(), b"")

    def test_script_never_persists_or_prints_request_material(self):
        text = SCRIPT.read_text()
        self.assertTrue(text.startswith("#!/usr/bin/python3 -I\n"))
        self.assertFalse(SCRIPT.stat().st_mode & 0o111, "script must not be executable; run it as python3 -I -B")
        self.assertIn('require(sys.flags.isolated if isolated is None else isolated, "isolated_mode_required")', text)
        self.assertIn("context.keylog_filename = None", text)
        self.assertNotIn("open(", text)
        self.assertNotIn("refreshToken", text)
        self.assertNotIn("?key=", text)
        self.assertNotIn("urllib", text)
        self.assertNotIn("subprocess", text)
        self.assertIn("x-goog-api-key", text)
        self.assertEqual(text.count("print("), text.count("file=stderr"))
        self.assertIn('require(len(argv) == 1, "arguments_not_accepted")', text)
        self.assertIn("MAX_INPUT_BYTES + 1", text)
        self.assertLess(text.index('"isolated_mode_required"'), text.index('"arguments_not_accepted"'))
        self.assertLess(text.index("require_private_stdin(fstat"), text.index("require_piped_stdout(fstat"))
        self.assertLess(text.index("require_piped_stdout(fstat"), text.index("stdin.buffer.read("))
        self.assertEqual(subject.PROJECT, "jeeb-development-msi")
        self.assertEqual(subject.PROJECT, helper.PROJECT)
        self.assertTrue(re.fullmatch(r"[A-Za-z0-9._-]{1,64}", subject.PROVIDER))

    def test_reviewed_helper_is_untouched(self):
        # The installed MSI gateway helper (infrastructure manifest and root install)
        # is pinned to this digest; this change must not alter it.
        digest = hashlib.sha256(HELPER.read_bytes()).hexdigest()
        self.assertEqual(digest, "8095616b956bb5c455c0b44f018612a79a6fd2570025e7c1bc7ee8d5dfe04438")
        self.assertEqual(helper.PROJECT, "jeeb-development-msi")

    def test_runbook_and_ci_pin_the_exact_pipeline(self):
        runbook = RUNBOOK.read_text()
        self.assertIn("scripts/mint-development-firebase-diagnostic-probe.py", runbook)
        self.assertIn("set -euo pipefail", runbook)
        self.assertIn(
            "gh secret set JEEB_DEVELOPMENT_FIREBASE_DIAGNOSTIC_PROBE_JSON",
            runbook,
        )
        self.assertIn(
            "gh secret delete JEEB_DEVELOPMENT_FIREBASE_DIAGNOSTIC_PROBE_JSON",
            runbook,
        )
        self.assertIn("--env development-msi-gateway-signing", runbook)
        self.assertIn('"expectedProvider":"password"', runbook)
        self.assertIn('"status":"probe_minted"', runbook)
        self.assertIn("umask 077", runbook)
        self.assertIn("python3 -I -B scripts/test_mint_development_firebase_diagnostic_probe.py", CI.read_text())


if __name__ == "__main__":
    unittest.main()
