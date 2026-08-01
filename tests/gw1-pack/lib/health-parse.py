#!/usr/bin/env python3
"""
GW1 TEST PACK — the reader for MSI's /health/ready payload, plus its own controls.

WHY A DEDICATED READER, rather than a grep in the probe script.
`GW1.md`'s V-2 row rests on one live string. This programme's recorded failure mode is
an instrument that reports confidently and wrongly (b02: ten checkers audited, ten
wrong), so the reader is separated from the thing being read and is shipped with the
fixtures that make it REJECT. An instrument only ever observed accepting is not an
instrument.

Three ways a naive reader gets this wrong, all of which the fixtures below reproduce:

  1. `"status":"Healthy"` on store-durability is ALSO what a Development/Testing host
     returns, with every critical store in process memory
     (StoreDurabilityHealthCheck: `if (IsExempt(env)) return Healthy("… exempt …")`).
     Status alone is worthless; the DESCRIPTION carries the information.
  2. A substring match on "critical stores durable" happily accepts "all 32 …", which is
     the pre-GW1 baseline — i.e. it accepts the state the batch exists to change.
  3. A `grep -c` style reader that finds no store-durability entry at all prints nothing
     and, under `set -e`, aborts before recording — or records the empty string as a zero.
     Absence must be an explicit REJECT.

Usage:
    health-parse.py --self-test                 # offline; runs every fixture control
    health-parse.py --expect-critical 33 [FILE] # FILE or stdin = a /health/ready payload

Exit 0 = ACCEPT. Exit 1 = REJECT (reason printed). Exit 2 = usage/parse error.
"""
import argparse
import json
import sys

CHECK_NAME = "store-durability"
EXEMPT_MARKER = "exempt"


def read_verdict(payload_text, expect_critical):
    """Return (accepted: bool, reason: str). Never raises on malformed input."""
    try:
        doc = json.loads(payload_text)
    except Exception as exc:  # noqa: BLE001 - a malformed payload is a REJECT, not a crash
        return False, "payload is not JSON: %s" % exc

    checks = doc.get("checks")
    if not isinstance(checks, list) or not checks:
        return False, "payload carries no 'checks' array (nothing was measured)"

    entry = next((c for c in checks if isinstance(c, dict) and c.get("name") == CHECK_NAME), None)
    if entry is None:
        return False, (
            "no '%s' entry in the payload. ABSENCE IS A REJECT: a reader that treats a "
            "missing check as 'nothing wrong' passes a gateway that never registered it"
            % CHECK_NAME
        )

    status = entry.get("status")
    description = entry.get("description") or ""

    if status != "Healthy":
        return False, "%s status is %r, not 'Healthy' — %s" % (CHECK_NAME, status, description)

    if EXEMPT_MARKER in description:
        return False, (
            "%s is Healthy but EXEMPT (%r). This is a Development/Testing host: the guard "
            "is a documented no-op there and every critical store may be in memory. "
            "Status alone proves nothing." % (CHECK_NAME, description)
        )

    expected = "%s: all %d critical stores durable" % (CHECK_NAME, expect_critical)
    if description != expected:
        return False, (
            "%s description is %r; the sealed predicate requires exactly %r"
            % (CHECK_NAME, description, expected)
        )

    return True, (
        "%s = Healthy, description byte-exact %r. Per StoreDurabilityHealthCheck this "
        "string is emitted ONLY when Evaluate() resolved every Critical interface from the "
        "LIVE container and matched its concrete runtime type against the approved durable "
        "set — so it is a live type read, not just an array length." % (CHECK_NAME, expected)
    )


# --- fixtures: the controls that prove this reader can reject -----------------
def _payload(entry, extra=None):
    checks = [{"name": "gateway-postgres", "status": "Healthy", "description": "GatewayPostgres reachable"}]
    if entry is not None:
        checks.append(entry)
    if extra:
        checks.extend(extra)
    return json.dumps({"status": "Healthy", "checks": checks, "failing": []})


FIXTURES = [
    # (id, payload, expect_critical, must_accept, what it models)
    ("F1-pos", _payload({"name": CHECK_NAME, "status": "Healthy",
                         "description": "store-durability: all 33 critical stores durable"}),
     33, True, "POSITIVE CONTROL: the post-GW1 MSI payload must be ACCEPTED"),

    ("F2-count", _payload({"name": CHECK_NAME, "status": "Healthy",
                           "description": "store-durability: all 32 critical stores durable"}),
     33, False, "the pre-GW1 baseline (32) must be REJECTED — a substring reader accepts it"),

    ("F3-exempt", _payload({"name": CHECK_NAME, "status": "Healthy",
                            "description": "store-durability: exempt (Development/Testing)"}),
     33, False, "a Development host is Healthy with everything in memory — must be REJECTED"),

    ("F4-unhealthy", _payload({"name": CHECK_NAME, "status": "Unhealthy",
                               "description": "store-durability: ISettlementLedgerClient resolved to "
                                              "InMemorySettlementLedgerClient"}),
     33, False, "the real red must be REJECTED and must name the store"),

    ("F5-absent", _payload(None),
     33, False, "no store-durability entry at all must be REJECTED, never read as clean"),

    ("F6-garbage", "this is not json",
     33, False, "a truncated/errored curl body must be REJECTED, not crash the probe"),

    ("F7-empty", json.dumps({"status": "Healthy", "checks": [], "failing": []}),
     33, False, "an empty checks array must be REJECTED (nothing was measured)"),

    ("F8-nearmiss", _payload({"name": CHECK_NAME, "status": "Healthy",
                              "description": "store-durability: all 33 critical stores durable "
                                             "(fail-closed disabled)"}),
     33, False, "a description with extra text must be REJECTED — the match is byte-exact"),
]


def self_test():
    bad = 0
    print("health-parse.py reader controls (offline, no MSI required)")
    for fid, payload, expect, must_accept, why in FIXTURES:
        accepted, reason = read_verdict(payload, expect)
        ok = accepted == must_accept
        verb = "ACCEPT" if accepted else "REJECT"
        print("  %-4s %-13s %s  %s" % ("OK" if ok else "BAD", fid, verb, why))
        if not ok:
            print("        -> reader said: %s" % reason)
            bad += 1
    print("  %d/%d controls behaved" % (len(FIXTURES) - bad, len(FIXTURES)))
    if bad:
        print("REFUSE: the reader could not be shown to behave; do not use it on live data.")
    return 1 if bad else 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--expect-critical", type=int, default=33)
    ap.add_argument("payload", nargs="?", help="file with a /health/ready body; default stdin")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    if args.payload:
        with open(args.payload, "r", encoding="utf-8") as fh:
            text = fh.read()
    else:
        text = sys.stdin.read()

    accepted, reason = read_verdict(text, args.expect_critical)
    print(("ACCEPT: " if accepted else "REJECT: ") + reason)
    return 0 if accepted else 1


if __name__ == "__main__":
    sys.exit(main())
