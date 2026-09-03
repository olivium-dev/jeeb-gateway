#!/usr/bin/env python3
"""Validate and safely materialize the protected Jeeb Firebase service account.

The credential is accepted from stdin so deployment workflows never place it in
an argument or log line.  Raw JSON and strict base64 are supported for backwards
compatibility with existing protected secrets.  Only the SHA-256 content address
is written to stdout.
"""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
import os
import sys
from pathlib import Path


EXPECTED_PROJECT_ID = "jeeb-5a293"
MAX_CREDENTIAL_BYTES = 256 * 1024


def decode_document(raw: bytes) -> bytes:
    if not raw or len(raw) > MAX_CREDENTIAL_BYTES:
        raise ValueError("credential is empty or exceeds the size limit")

    candidates = [raw]
    try:
        candidates.append(base64.b64decode(raw, validate=True))
    except (binascii.Error, ValueError):
        pass

    for candidate in candidates:
        try:
            document = json.loads(candidate)
        except (json.JSONDecodeError, UnicodeDecodeError):
            continue
        if not isinstance(document, dict):
            continue
        if document.get("type") != "service_account":
            raise ValueError("credential type must be service_account")
        if document.get("project_id") != EXPECTED_PROJECT_ID:
            raise ValueError(f"credential project_id must be {EXPECTED_PROJECT_ID}")
        for field in ("client_email", "private_key"):
            if not isinstance(document.get(field), str) or not document[field].strip():
                raise ValueError(f"credential field {field} is required")
        if "BEGIN PRIVATE KEY" not in document["private_key"]:
            raise ValueError("credential private_key is not a PEM private key")
        return candidate

    raise ValueError("credential is not a JSON service-account document")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--materialize",
        type=Path,
        required=True,
        help="restricted temporary file to receive the decoded JSON",
    )
    args = parser.parse_args()

    try:
        document = decode_document(sys.stdin.buffer.read(MAX_CREDENTIAL_BYTES + 1))
        descriptor = os.open(
            args.materialize,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL,
            0o600,
        )
        with os.fdopen(descriptor, "wb") as destination:
            destination.write(document)
    except (OSError, ValueError) as error:
        parser.error(str(error))

    print(hashlib.sha256(document).hexdigest())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
