#!/usr/bin/env python3
"""Offline contract and secret-redaction tests for the live Firebase smoke."""

from __future__ import annotations

import importlib.util
import io
import json
import unittest
import urllib.error
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
    environment = {
        "JEEB_TOKEN_MINT_KEY": "protected-mint-key",
        "JEEB_FIREBASE_WEB_API_KEY": "protected-web-api-key",
    }

    def test_happy_path_posts_exact_identity_chain_without_secret_output(self) -> None:
        opener = FakeOpen([
            Response({"accessToken": "jeeb-bearer"}),
            Response({"token": "firebase-custom-token", "uid": self.uid}),
            Response({"localId": self.uid, "idToken": "firebase-id-token"}),
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
