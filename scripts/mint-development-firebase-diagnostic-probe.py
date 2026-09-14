#!/usr/bin/python3 -I
"""Mint the short-lived development Firebase diagnostic probe in process memory.

The MSI gateway helper activates Firebase token diagnostics only after it has
proved that one real ``jeeb-development-msi`` ID token is accepted on both
route twins and that a malformed token is rejected. That valid token reaches
the helper through the protected ``development-msi-gateway-signing``
environment secret ``JEEB_DEVELOPMENT_FIREBASE_DIAGNOSTIC_PROBE_JSON``. This
script produces exactly that secret's document and nothing else:

* it reads one owner-only, mode-0600, regular-file JSON input from standard
  input (development Web API key, the explicitly approved non-production test
  identity, its password, and the expected Firebase uid);
* it performs exactly one Identity Toolkit password sign-in for that identity
  and discards the refresh token;
* it binds the returned ID token's non-secret claims to the fixed project,
  issuer, provider, lifetime, and expected uid. This inspects Google's own
  HTTPS response; it is not local signature verification and must never be
  used to authenticate caller-supplied tokens;
* it writes only ``{"idToken","expectedSubject","expectedProvider"}`` to a
  piped standard output so the owner streams it straight into ``gh secret
  set``.

The API key, password, refresh token, and ID token never enter argv, files,
logs, or standard error. Standard error carries one fixed evidence document
with bounded hashes only. Buffers are cleared on exit on a best-effort basis;
Python cannot guarantee zeroization of immutable objects, so the process exits
immediately after emitting the probe.
"""
from __future__ import annotations

import base64
import hashlib
import http.client
import json
import os
import re
import ssl
import stat
import sys
import time

PROJECT = "jeeb-development-msi"
ISSUER = f"https://securetoken.google.com/{PROJECT}"
PROVIDER = "password"
IDENTITY_TOOLKIT_HOST = "identitytoolkit.googleapis.com"
SIGN_IN_PATH = "/v1/accounts:signInWithPassword"
API_KEY_HEADER = "x-goog-api-key"
MAX_INPUT_BYTES = 4096
MAX_RESPONSE_BYTES = 65536
MAX_ID_TOKEN_BYTES = 16 * 1024
MAX_SUBJECT_LENGTH = 128
MAX_TOKEN_LIFETIME = 3600
MIN_REMAINING_LIFETIME = 1800
CLOCK_SKEW = 60
INPUT_FIELDS = frozenset({"webApiKey", "email", "password", "expectedUid"})
PROBE_FIELDS = ("idToken", "expectedSubject", "expectedProvider")
EVIDENCE_FIELDS = frozenset(
    {"status", "project", "provider", "uidSha256Prefix", "tokenSha256Prefix", "expiresAt"}
)


class Rejected(Exception):
    """Fixed reason only; provider bodies and secret material are never attached."""


def require(condition, reason):
    if not condition:
        raise Rejected(reason)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate_key")
        result[key] = value
    return result


def parse_json(raw, reason):
    try:
        return json.loads(raw, object_pairs_hook=unique_object)
    except Rejected:
        raise
    except Exception:
        raise Rejected(reason) from None


def validate_input(raw):
    require(0 < len(raw) <= MAX_INPUT_BYTES, "input_size_invalid")
    value = parse_json(raw, "input_json_invalid")
    require(isinstance(value, dict) and set(value) == INPUT_FIELDS, "input_contract_invalid")
    require(all(isinstance(item, str) and item for item in value.values()), "input_contract_invalid")
    require(re.fullmatch(r"[A-Za-z0-9_-]{20,128}", value["webApiKey"]), "web_api_key_invalid")
    require(
        len(value["email"]) <= 254 and re.fullmatch(r"[^\s@]+@[^\s@]+\.[^\s@]+", value["email"]),
        "email_invalid",
    )
    require(0 < len(value["password"].encode()) <= 1024, "password_invalid")
    require(
        re.fullmatch(r"[A-Za-z0-9._:-]{1,%d}" % MAX_SUBJECT_LENGTH, value["expectedUid"]),
        "expected_uid_invalid",
    )
    return value


def https_post(path, headers, body):
    # Direct HTTPS: no proxy variables, no redirects, bounded read, closed connection.
    context = ssl.create_default_context()
    # Isolated mode already ignores SSLKEYLOGFILE; never let TLS keys reach a file.
    # LibreSSL builds lack the attribute, so guard it the way the stdlib does.
    if hasattr(context, "keylog_filename"):
        context.keylog_filename = None
    connection = http.client.HTTPSConnection(IDENTITY_TOOLKIT_HOST, timeout=15, context=context)
    try:
        connection.request("POST", path, body, headers)
        response = connection.getresponse()
        return response.status, response.read(MAX_RESPONSE_BYTES + 1)
    finally:
        connection.close()


def sign_in(value, post):
    body = json.dumps(
        {"email": value["email"], "password": value["password"], "returnSecureToken": True},
        separators=(",", ":"),
    ).encode()
    headers = {
        "Content-Type": "application/json",
        "Connection": "close",
        API_KEY_HEADER: value["webApiKey"],
    }
    try:
        status, raw = post(SIGN_IN_PATH, headers, body)
    except (OSError, http.client.HTTPException):
        raise Rejected("sign_in_unreachable") from None
    require(status == 200, f"sign_in_http_{int(status)}")
    require(len(raw) <= MAX_RESPONSE_BYTES, "response_too_large")
    response = parse_json(raw, "response_json_invalid")
    require(isinstance(response, dict), "response_shape_invalid")
    require(response.get("localId") == value["expectedUid"], "identity_mismatch")
    require(response.get("registered") is True, "identity_not_registered")
    token = response.get("idToken")
    require(isinstance(token, str) and 0 < len(token) <= MAX_ID_TOKEN_BYTES, "id_token_invalid")
    expires_in = response.get("expiresIn")
    require(
        isinstance(expires_in, str) and expires_in.isdigit() and 0 < int(expires_in) <= MAX_TOKEN_LIFETIME,
        "expires_in_invalid",
    )
    return token


def b64url_decode(part):
    require(re.fullmatch(r"[A-Za-z0-9_-]+", part), "id_token_shape_invalid")
    try:
        return base64.urlsafe_b64decode(part + "=" * (-len(part) % 4))
    except Exception:
        raise Rejected("id_token_shape_invalid") from None


def bind_claims(token, uid, now):
    parts = token.split(".")
    require(len(parts) == 3, "id_token_shape_invalid")
    require(re.fullmatch(r"[A-Za-z0-9_-]+", parts[2]), "id_token_shape_invalid")
    header = parse_json(b64url_decode(parts[0]), "id_token_shape_invalid")
    claims = parse_json(b64url_decode(parts[1]), "id_token_shape_invalid")
    require(
        isinstance(header, dict)
        and header.get("alg") == "RS256"
        and isinstance(header.get("kid"), str)
        and header["kid"] != "",
        "id_token_header_invalid",
    )
    require(isinstance(claims, dict), "id_token_claims_invalid")
    require(claims.get("aud") == PROJECT, "audience_mismatch")
    require(claims.get("iss") == ISSUER, "issuer_mismatch")
    require(claims.get("sub") == uid and claims.get("user_id") == uid, "subject_mismatch")
    firebase = claims.get("firebase")
    require(
        isinstance(firebase, dict) and firebase.get("sign_in_provider") == PROVIDER,
        "provider_mismatch",
    )
    issued_at, expires_at = claims.get("iat"), claims.get("exp")
    require(type(issued_at) is int and type(expires_at) is int, "lifetime_invalid")
    require(
        issued_at <= now + CLOCK_SKEW and now < expires_at <= issued_at + MAX_TOKEN_LIFETIME,
        "lifetime_invalid",
    )
    require(expires_at - now >= MIN_REMAINING_LIFETIME, "lifetime_too_short")
    return expires_at


def mint(raw, post=https_post, now=time.time):
    value = validate_input(raw)
    token = sign_in(value, post)
    expires_at = bind_claims(token, value["expectedUid"], int(now()))
    probe = json.dumps(
        {"idToken": token, "expectedSubject": value["expectedUid"], "expectedProvider": PROVIDER},
        separators=(",", ":"),
    ).encode() + b"\n"
    evidence = {
        "status": "probe_minted",
        "project": PROJECT,
        "provider": PROVIDER,
        "uidSha256Prefix": hashlib.sha256(value["expectedUid"].encode()).hexdigest()[:16],
        "tokenSha256Prefix": hashlib.sha256(token.encode()).hexdigest()[:16],
        "expiresAt": expires_at,
    }
    return probe, evidence


def require_private_stdin(info, euid):
    require(stat.S_ISREG(info.st_mode), "stdin_must_be_regular_file")
    require(stat.S_IMODE(info.st_mode) == 0o600, "stdin_mode_must_be_0600")
    require(info.st_uid == euid, "stdin_owner_invalid")
    require(0 < info.st_size <= MAX_INPUT_BYTES, "input_size_invalid")


def require_piped_stdout(info):
    require(stat.S_ISFIFO(info.st_mode), "stdout_must_be_pipe")


def run(argv, stdin, stdout, stderr, fstat=os.fstat, euid=os.geteuid, isolated=None, minter=mint):
    raw = None
    probe = None
    try:
        require(sys.flags.isolated if isolated is None else isolated, "isolated_mode_required")
        require(len(argv) == 1, "arguments_not_accepted")
        require_private_stdin(fstat(stdin.fileno()), euid())
        require_piped_stdout(fstat(stdout.fileno()))
        raw = bytearray(stdin.buffer.read(MAX_INPUT_BYTES + 1))
        minted, evidence = minter(bytes(raw))
        probe = bytearray(minted)
        stdout.buffer.write(probe)
        stdout.buffer.flush()
        print(json.dumps(evidence, separators=(",", ":")), file=stderr)
        return 0
    except (Exception, KeyboardInterrupt) as exc:
        reason = exc.args[0] if isinstance(exc, Rejected) and exc.args else "execution_failed"
        print(json.dumps({"status": "probe_rejected", "reason": reason}, separators=(",", ":")), file=stderr)
        return 1
    finally:
        for buffer in (raw, probe):
            if buffer is not None:
                buffer[:] = b"\0" * len(buffer)


def main():
    return run(sys.argv, sys.stdin, sys.stdout, sys.stderr)


if __name__ == "__main__":
    raise SystemExit(main())
