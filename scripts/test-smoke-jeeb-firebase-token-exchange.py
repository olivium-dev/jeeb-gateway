#!/usr/bin/env python3
"""Offline contract and secret-redaction tests for the live Firebase smoke."""

from __future__ import annotations

import importlib.util
import base64
import contextlib
import io
import json
import unittest
import urllib.error
from unittest.mock import patch
from pathlib import Path
from typing import Any


SCRIPT = Path(__file__).with_name("smoke-jeeb-firebase-token-exchange.py")
SPEC = importlib.util.spec_from_file_location("firebase_exchange_smoke", SCRIPT)
assert SPEC and SPEC.loader
smoke = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(smoke)


class Response:
    def __init__(self, payload: dict[str, object], status: int = 200) -> None:
        self.status = status
        self._body = json.dumps(payload).encode("utf-8")

    def __enter__(self) -> "Response":
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def read(self, limit: int) -> bytes:
        return self._body[:limit]


class FakeOpen:
    def __init__(self, responses: list[Response | Exception]) -> None:
        self.responses = responses
        self.requests: list[Any] = []

    def __call__(self, request: Any, timeout: int) -> Response:
        self.requests.append(request)
        response = self.responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response


class FirebaseExchangeSmokeTests(unittest.TestCase):
    uid = "live-smoke-user"
    now = 1_800_000_000
    environment = {
        "JEEB_TOKEN_MINT_KEY": "protected-mint-key",
        "JEEB_FIREBASE_WEB_API_KEY": "protected-web-api-key",
    }

    def token(self, claims: Any = None, header: Any = None) -> str:
        if claims is None:
            claims = self.claims()
        if header is None:
            header = {"alg": "RS256", "typ": "JWT"}
        encoded = [base64.urlsafe_b64encode(json.dumps(value).encode()).decode().rstrip("=")
                   for value in (header, claims)]
        # Synthetic signature: these tests assert response claim binding, NOT
        # cryptographic verification of caller-supplied JWTs.
        return ".".join([*encoded, "c3ludGhldGljLXNpZ25hdHVyZQ"])

    def claims(self) -> dict[str, object]:
        return {"aud": smoke.PROJECT_ID,
                "iss": "https://securetoken.google.com/" + smoke.PROJECT_ID,
                "sub": self.uid, "exp": self.now + 3600}

    def setUp(self) -> None:
        clock = patch.object(smoke.time, "time", return_value=self.now)
        clock.start()
        self.addCleanup(clock.stop)

    def test_happy_path_posts_exact_identity_chain_without_secret_output(self) -> None:
        opener = FakeOpen([
            Response({"accessToken": "jeeb-bearer"}),
            Response({"token": "firebase-custom-token", "uid": self.uid}),
            Response({"localId": self.uid, "idToken": self.token()}),
        ])

        smoke.run_smoke(
            "https://jeeb.fds-1.com/", self.uid, dict(self.environment), opener
        )

        self.assertEqual(len(opener.requests), 3)
        first, second, third = opener.requests
        self.assertEqual(first.full_url, "https://jeeb.fds-1.com/auth/tokens")
        self.assertEqual(json.loads(first.data), {"userId": self.uid, "roles": ["client"]})
        self.assertEqual(first.get_header("X-service-auth-key"), "protected-mint-key")
        self.assertEqual(second.full_url, "https://jeeb.fds-1.com/v1/chat/firebase-token")
        self.assertEqual(second.get_header("Authorization"), "Bearer jeeb-bearer")
        self.assertEqual(json.loads(second.data), {})
        self.assertEqual(
            third.full_url,
            "https://identitytoolkit.googleapis.com/v1/"
            "accounts:signInWithCustomToken?key=protected-web-api-key",
        )
        self.assertEqual(
            json.loads(third.data),
            {"token": "firebase-custom-token", "returnSecureToken": True},
        )

    def test_invalid_claims_fail_after_exchange_without_disclosure(self) -> None:
        for key, value in (
            ("aud", "cross-project-private-sentinel"), ("aud", [smoke.PROJECT_ID]),
            ("iss", "https://attacker.invalid/private-sentinel"),
            ("iss", "http://securetoken.google.com/" + smoke.PROJECT_ID),
            ("sub", "other-private-actor-sentinel"), ("sub", None),
            ("exp", self.now), ("exp", self.now - 1),
            ("exp", self.now + 3661), ("exp", str(self.now + 3600)),
            ("exp", True), ("exp", None), ("exp", float("nan")),
        ):
            with self.subTest(key=key):
                claims = self.claims()
                claims[key] = value
                self.assert_binding_rejected(self.token(claims))

    def assert_binding_rejected(self, token: str) -> None:
        opener = FakeOpen([
            Response({"accessToken": "jeeb-bearer"}),
            Response({"token": "firebase-custom-token", "uid": self.uid}),
            Response({"localId": self.uid, "idToken": token}),
        ])
        output = io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
            with self.assertRaises(smoke.SmokeFailure) as raised:
                smoke.run_smoke("https://jeeb.fds-1.com", self.uid,
                                dict(self.environment), opener)
        self.assertEqual(len(opener.requests), 3)
        self.assertEqual(str(raised.exception),
                         "Firebase Identity Toolkit returned an invalid identity binding")
        self.assertEqual(output.getvalue(), "")
        self.assertIsNone(raised.exception.__cause__)
        self.assertTrue(raised.exception.__suppress_context__)

    def test_malformed_compact_tokens_are_rejected(self) -> None:
        valid = self.token()
        header, payload, signature = valid.split(".")
        encode = lambda value: base64.urlsafe_b64encode(value).decode().rstrip("=")
        duplicate = json.dumps(self.claims())[:-1] + ', "aud": "duplicate-private-sentinel"}'
        for token in (
            "private-token-sentinel", valid + ".extra", "." + payload + "." + signature,
            header + ".*." + signature, header + ".a." + signature,
            header + "." + encode(b"not-json-private-sentinel") + "." + signature,
            header + "." + encode(b"\xff") + "." + signature,
            header + "." + encode(duplicate.encode()) + "." + signature,
            header + "." + encode(b"[" * 1500 + b"]" * 1500) + "." + signature,
            self.token([]), self.token(header=[]), self.token(header={"alg": "none"}),
            "a" * (smoke.MAX_ID_TOKEN_BYTES + 1),
        ):
            with self.subTest():
                self.assert_binding_rejected(token)

    def test_live_expiry_boundaries_are_explicit(self) -> None:
        for expiry in (self.now + 1, self.now + 3660):
            claims = self.claims()
            claims["exp"] = expiry
            smoke._identity_claims_bound(self.token(claims), self.uid)

    def test_gateway_uid_mismatch_is_rejected_before_firebase_exchange(self) -> None:
        opener = FakeOpen([
            Response({"accessToken": "jeeb-bearer"}),
            Response({"token": "firebase-custom-token", "uid": "another-user"}),
        ])
        with self.assertRaisesRegex(smoke.SmokeFailure, "wrong uid"):
            smoke.run_smoke(
                "https://app.jeeb.fds-1.com", self.uid, dict(self.environment), opener
            )
        self.assertEqual(len(opener.requests), 2)

    def test_identity_toolkit_uid_mismatch_is_rejected(self) -> None:
        opener = FakeOpen([
            Response({"accessToken": "jeeb-bearer"}),
            Response({"token": "firebase-custom-token", "uid": self.uid}),
            Response({"localId": "another-user", "idToken": "firebase-id-token"}),
        ])
        with self.assertRaisesRegex(smoke.SmokeFailure, "wrong uid"):
            smoke.run_smoke(
                "https://jeeb.fds-1.com", self.uid, dict(self.environment), opener
            )

    def test_unapproved_origin_and_missing_secrets_are_rejected_without_network(self) -> None:
        opener = FakeOpen([])
        with self.assertRaisesRegex(smoke.SmokeFailure, "approved Jeeb HTTPS"):
            smoke.run_smoke("https://attacker.example", self.uid, dict(self.environment), opener)
        with self.assertRaisesRegex(smoke.SmokeFailure, "JEEB_TOKEN_MINT_KEY"):
            smoke.run_smoke("https://jeeb.fds-1.com", self.uid, {}, opener)
        self.assertEqual(opener.requests, [])

    def test_http_failure_redacts_url_body_and_all_tokens(self) -> None:
        api_key = self.environment["JEEB_FIREBASE_WEB_API_KEY"]
        error = urllib.error.HTTPError(
            f"https://identitytoolkit.googleapis.com/v1/x?key={api_key}",
            403,
            "firebase-custom-token firebase-id-token",
            {},
            io.BytesIO(b"firebase-custom-token firebase-id-token"),
        )
        opener = FakeOpen([
            Response({"accessToken": "jeeb-bearer"}),
            Response({"token": "firebase-custom-token", "uid": self.uid}),
            error,
        ])
        with self.assertRaises(smoke.SmokeFailure) as raised:
            smoke.run_smoke(
                "https://jeeb.fds-1.com", self.uid, dict(self.environment), opener
            )
        message = str(raised.exception)
        self.assertEqual(message, "Firebase Identity Toolkit exchange returned HTTP 403")
        for secret in (api_key, "jeeb-bearer", "firebase-custom-token", "firebase-id-token"):
            self.assertNotIn(secret, message)


if __name__ == "__main__":
    unittest.main()
