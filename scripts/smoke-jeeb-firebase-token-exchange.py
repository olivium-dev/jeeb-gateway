#!/usr/bin/env python3
"""Secret-safe live proof of Jeeb bearer -> Firebase identity exchange.

This is deliberately a protected/manual smoke. It keeps the gateway mint key,
Jeeb bearer, Firebase custom token, Firebase Web API key, and Firebase ID token
in this process only; none are printed or placed in command-line arguments.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Callable
from typing import Any


PROJECT_ID = "jeeb-5a293"
ALLOWED_GATEWAY_ORIGINS = {
    "https://app.jeeb.fds-1.com",
    "https://jeeb.fds-1.com",
}
MAX_RESPONSE_BYTES = 1_048_576
OpenUrl = Callable[..., Any]


class SmokeFailure(RuntimeError):
    """Sanitized smoke failure; never include a response body or credential."""


def _required_secret(environment: dict[str, str], name: str) -> str:
    value = environment.get(name, "")
    if not value or len(value) > 4096 or any(character.isspace() for character in value):
        raise SmokeFailure(f"required protected input {name} is missing or malformed")
    return value


def _validated_origin(origin: str) -> str:
    candidate = origin.rstrip("/")
    if candidate not in ALLOWED_GATEWAY_ORIGINS:
        raise SmokeFailure("gateway origin is not an approved Jeeb HTTPS origin")
    return candidate


def _validated_uid(uid: str) -> str:
    if not uid or len(uid.encode("utf-8")) > 128 or any(ord(character) < 32 for character in uid):
        raise SmokeFailure("expected Firebase uid is missing or malformed")
    return uid


def _post_json(
    url: str,
    payload: dict[str, object],
    headers: dict[str, str],
    operation: str,
    open_url: OpenUrl,
) -> dict[str, object]:
    request = urllib.request.Request(
        url,
        data=json.dumps(payload, separators=(",", ":")).encode("utf-8"),
        headers={"Content-Type": "application/json", **headers},
        method="POST",
    )
    try:
        with open_url(request, timeout=15) as response:
            status = int(getattr(response, "status", 200))
            raw = response.read(MAX_RESPONSE_BYTES + 1)
    except urllib.error.HTTPError as error:
        raise SmokeFailure(f"{operation} returned HTTP {error.code}") from None
    except (urllib.error.URLError, TimeoutError, OSError):
        raise SmokeFailure(f"{operation} could not reach its approved endpoint") from None

    if status < 200 or status >= 300:
        raise SmokeFailure(f"{operation} returned HTTP {status}")
    if len(raw) > MAX_RESPONSE_BYTES:
        raise SmokeFailure(f"{operation} response exceeded the safety limit")
    try:
        document = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise SmokeFailure(f"{operation} returned invalid JSON") from None
    if not isinstance(document, dict):
        raise SmokeFailure(f"{operation} returned a non-object JSON response")
    return document


def run_smoke(
    gateway_origin: str,
    expected_uid: str,
    environment: dict[str, str] | None = None,
    open_url: OpenUrl = urllib.request.urlopen,
) -> None:
    environment = os.environ if environment is None else environment
    origin = _validated_origin(gateway_origin)
    uid = _validated_uid(expected_uid)
    mint_key = _required_secret(environment, "JEEB_TOKEN_MINT_KEY")
    firebase_api_key = _required_secret(environment, "JEEB_FIREBASE_WEB_API_KEY")

    mint = _post_json(
        f"{origin}/auth/tokens",
        {"userId": uid, "roles": ["client"]},
        {"X-Service-Auth-Key": mint_key},
        "gateway bearer mint",
        open_url,
    )
    bearer = mint.get("accessToken")
    if not isinstance(bearer, str) or not bearer:
        raise SmokeFailure("gateway bearer mint omitted accessToken")

    custom = _post_json(
        f"{origin}/v1/chat/firebase-token",
        {},
        {"Authorization": f"Bearer {bearer}"},
        "gateway Firebase custom-token mint",
        open_url,
    )
    custom_token = custom.get("token")
    returned_uid = custom.get("uid")
    if not isinstance(custom_token, str) or not custom_token:
        raise SmokeFailure("gateway Firebase custom-token mint omitted token")
    if returned_uid != uid:
        raise SmokeFailure("gateway Firebase custom-token mint returned the wrong uid")

    endpoint = (
        "https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken?key="
        + urllib.parse.quote(firebase_api_key, safe="")
    )
    exchanged = _post_json(
        endpoint,
        {"token": custom_token, "returnSecureToken": True},
        {},
        "Firebase Identity Toolkit exchange",
        open_url,
    )
    if exchanged.get("localId") != uid:
        raise SmokeFailure("Firebase Identity Toolkit returned the wrong uid")
    if not isinstance(exchanged.get("idToken"), str) or not exchanged["idToken"]:
        raise SmokeFailure("Firebase Identity Toolkit omitted idToken")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gateway-origin", required=True)
    parser.add_argument("--expected-uid", required=True)
    args = parser.parse_args()
    try:
        run_smoke(args.gateway_origin, args.expected_uid)
    except SmokeFailure as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    print(f"Jeeb Firebase token exchange is valid for project {PROJECT_ID}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
