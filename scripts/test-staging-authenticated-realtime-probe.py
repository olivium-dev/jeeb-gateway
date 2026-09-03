#!/usr/bin/env python3
"""Offline compatibility tests for the staging authenticated realtime probe."""

from __future__ import annotations

import importlib.util
import sys
from datetime import datetime, timezone
from pathlib import Path


sys.dont_write_bytecode = True
probe_path = Path(__file__).with_name("probe-staging-authenticated-realtime.py")
spec = importlib.util.spec_from_file_location("staging_realtime_probe", probe_path)
if spec is None or spec.loader is None:
    raise RuntimeError("could not load the staging authenticated realtime probe")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


assert probe.parse_rfc3339("2026-08-25T11:04:52.8184119+00:00") == datetime(
    2026, 8, 25, 11, 4, 52, 818411, tzinfo=timezone.utc
)
assert probe.parse_rfc3339("2026-08-25T11:04:52.1Z") == datetime(
    2026, 8, 25, 11, 4, 52, 100000, tzinfo=timezone.utc
)
assert probe.parse_rfc3339("2026-08-25T13:34:52+02:30").astimezone(
    timezone.utc
) == datetime(2026, 8, 25, 11, 4, 52, tzinfo=timezone.utc)

for invalid in (
    None,
    "",
    "2026-08-25T11:04:52",
    "2026-08-25T11:04:52.81841199Z",
    "2026-13-25T11:04:52Z",
):
    try:
        probe.parse_rfc3339(invalid)
    except RuntimeError:
        pass
    else:
        raise AssertionError(f"invalid RFC3339 value was accepted: {invalid!r}")

print("Staging authenticated realtime RFC3339 compatibility: PASS")

# The 2026-09-03 staging failure printed a bare "did not return 101" and cost a
# full investigation; the message must now carry the status and safe headers.
unmapped_route = probe.describe_upgrade_failure(
    "HTTP/1.1 401 Unauthorized",
    [
        "Content-Type: application/problem+json",
        "WWW-Authenticate: Bearer",
        "X-Correlation-Id: 40447510-cd5e-454d-bfa2-ca284d5aada4",
        "Strict-Transport-Security: max-age=31536000",
        "malformed-header-without-separator",
    ],
)
assert "authenticated WebSocket upgrade did not return 101" in unmapped_route
assert "status='HTTP/1.1 401 Unauthorized'" in unmapped_route
assert "content-type=application/problem+json" in unmapped_route
assert "www-authenticate=Bearer" in unmapped_route
for redacted in ("x-correlation-id", "strict-transport-security", "malformed-header"):
    assert redacted not in unmapped_route, redacted

proxied_rejection = probe.describe_upgrade_failure(
    "HTTP/1.1 403 Forbidden", ["x-jeeb-realtime-proxy: gateway"]
)
assert "x-jeeb-realtime-proxy=gateway" in proxied_rejection
assert probe.describe_upgrade_failure("HTTP/1.1 502 Bad Gateway", []).endswith(
    "headers=[])"
)

print("Staging authenticated realtime upgrade diagnostics: PASS")
