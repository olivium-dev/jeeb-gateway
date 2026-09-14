import base64
import hashlib
import importlib.util
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
FIXTURE_KEY = "fixture-web-api-key-0123456789ab"
FIXTURE_EMAIL = "probe@example.invalid"
FIXTURE_PASSWORD = "fixture-password-value"


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
    claims.update(changes)
    return ".".join(
        (
            b64url(json.dumps(header, separators=(",", ":")).encode()),
            b64url(json.dumps(claims, separators=(",", ":")).encode()),
            b64url(b"fixture-signature-bytes"),
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
        "refreshToken": "fixture-refresh-token-must-be-discarded",
        "expiresIn": "3600",
    }
    value.update(changes)
    return json.dumps(value).encode()


class Recorder:
    def __init__(self, status=200, body=None):
        self.status = status
        self.body = response() if body is None else body
        self.calls = []

    def __call__(self, path, headers, body):
        self.calls.append((path, dict(headers), bytes(body)))
        return self.status, self.body


def stat_result(mode, uid=0, size=64):
    return os.stat_result((mode, 1, 1, 1, uid, uid, size, 0, 0, 0))


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
        self.assertEqual(helper.validate_probe(probe.rstrip(b"\n")), value)
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
            for raw in inputs:
                with self.subTest(reason=reason, raw=raw[:24]):
                    post = Recorder()
                    with self.assertRaises(subject.Rejected) as caught:
                        subject.mint(raw, post=post, now=lambda: NOW)
                    self.assertEqual(caught.exception.args[0], reason)
                    self.assertEqual(post.calls, [])

    def test_provider_response_binding_rejects_every_mismatch(self):
        cases = [
            ("sign_in_http_400", Recorder(status=400, body=b'{"error":{"message":"fixture"}}')),
            ("sign_in_http_503", Recorder(status=503, body=b"")),
            ("response_json_invalid", Recorder(body=b"{broken")),
            ("response_shape_invalid", Recorder(body=b"[]")),
            ("identity_mismatch", Recorder(body=response(localId="someone-else"))),
            ("identity_not_registered", Recorder(body=response(registered=False))),
            ("id_token_invalid", Recorder(body=response(idToken=""))),
            ("id_token_invalid", Recorder(body=response(idToken="x" * (subject.MAX_ID_TOKEN_BYTES + 1)))),
            ("expires_in_invalid", Recorder(body=response(expiresIn=3600))),
            ("expires_in_invalid", Recorder(body=response(expiresIn="86400"))),
            ("id_token_shape_invalid", Recorder(body=response(idToken="only.two"))),
            ("id_token_shape_invalid", Recorder(body=response(idToken="invalid.jwt.diagnostic-probe"))),
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
        ]
        for reason, guard in guards:
            with self.subTest(reason=reason):
                with self.assertRaises(subject.Rejected) as caught:
                    guard()
                self.assertEqual(caught.exception.args[0], reason)

    def test_script_never_persists_or_prints_request_material(self):
        text = SCRIPT.read_text()
        self.assertNotIn("open(", text)
        self.assertNotIn("refreshToken", text)
        self.assertNotIn("?key=", text)
        self.assertNotIn("urllib", text)
        self.assertNotIn("subprocess", text)
        self.assertIn("x-goog-api-key", text)
        self.assertEqual(text.count("print("), text.count("file=sys.stderr"))
        self.assertIn('require(len(sys.argv) == 1, "arguments_not_accepted")', text)
        self.assertIn("MAX_INPUT_BYTES + 1", text)
        self.assertEqual(subject.PROJECT, "jeeb-development-msi")
        self.assertEqual(subject.PROJECT, helper.PROJECT)
        self.assertTrue(re.fullmatch(r"[A-Za-z0-9._-]{1,64}", subject.PROVIDER))

    def test_runbook_and_ci_pin_the_exact_pipeline(self):
        runbook = RUNBOOK.read_text()
        self.assertIn("scripts/mint-development-firebase-diagnostic-probe.py", runbook)
        self.assertIn(
            "gh secret set JEEB_DEVELOPMENT_FIREBASE_DIAGNOSTIC_PROBE_JSON",
            runbook,
        )
        self.assertIn(
            "gh secret delete JEEB_DEVELOPMENT_FIREBASE_DIAGNOSTIC_PROBE_JSON",
            runbook,
        )
        self.assertEqual(runbook.count("--env development-msi-gateway-signing"), 2)
        self.assertIn('"expectedProvider":"password"', runbook)
        self.assertIn("umask 077", runbook)
        self.assertIn("python3 -I -B scripts/test_mint_development_firebase_diagnostic_probe.py", CI.read_text())


if __name__ == "__main__":
    unittest.main()
