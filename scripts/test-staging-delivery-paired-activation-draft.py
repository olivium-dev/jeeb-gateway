"""Hermetic draft ordering only: no Docker, SSH, credentials, or durable adapter."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SETUP = r'''
source "$1/scripts/staging-delivery-paired-activation-draft.sh"
gate() { printf '%s\n' "$1" >> "$FIXTURE/events"; [ "$FAIL" != "$1" ]; }
staging_gateway_lock_assert() { gate lock; }
paired_require_current_protected_builds() { gate authority; }
paired_require_exact_daemon() { gate daemon; }
paired_require_existing_credential() { gate credential; }
paired_validate_narrow_candidates() { gate candidate; }
paired_probe_delivery_schema() { gate schema; }
paired_verify_gateway() { gate gateway-proof; }
paired_verify_delivery() { gate delivery-proof; }
paired_verify_authenticated_wire() { gate wire-proof; }
paired_verify_both_final() { gate both-final; }
paired_journal_begin() {
  gate journal-begin || return 1
  [ ! -e "$FIXTURE/journal" ] || return 1
  printf prepared > "$FIXTURE/journal"
}
paired_journal_advance() {
  gate "$1" || return 1
  case "$(cat "$FIXTURE/journal"):$1" in
    prepared:gateway-submission-pending|gateway-submission-pending:gateway-verified|gateway-verified:delivery-submission-pending|delivery-submission-pending:delivery-verified|delivery-verified:complete) ;;
    *) return 1 ;;
  esac
  printf '%s' "$1" > "$FIXTURE/journal"
}
paired_capture_role() {
  cp "$FIXTURE/$1-current.json" "$2"
  cat "$FIXTURE/$1-version" > "$3"
  cat "$FIXTURE/$1-incumbent-id" > "$4"
}
paired_submit_role_cas() {
  printf 'POST-%s\n' "$1" >> "$FIXTURE/events"
  [ "$(cat "$FIXTURE/journal")" = "$1-submission-pending" ] || return 1
  [ "$2" = "${1}service" ] && [ "$3" = 7 ] || return 1
  cp "$4" "$FIXTURE/$1-current.json"
  printf 8 > "$FIXTURE/$1-version"
  if [ "$FAIL" = "$1-lost-ack" ]; then printf 000; else printf 200; fi
}
if [ "$FAIL" = missing-adapter ]; then unset -f paired_verify_authenticated_wire; fi
staging_delivery_paired_activation_draft "$FIXTURE"
'''


class DraftOrderingTests(unittest.TestCase):
    def run_draft(self, failure="", existing=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for role in ("gateway", "delivery"):
                for name in ("incumbent", "current"):
                    (root / f"{role}-{name}.json").write_text(json.dumps({"Name": role, "Image": "old"}))
                (root / f"{role}-candidate.json").write_text(json.dumps({"Name": role, "Image": "new"}))
                for name in ("incumbent-version", "version"):
                    (root / f"{role}-{name}").write_text("7")
                (root / f"{role}-incumbent-id").write_text(role + "service")
            if existing:
                (root / "journal").write_text("gateway-submission-pending")
            result = subprocess.run(["bash", "-euc", SETUP, "fixture", str(ROOT)],
                                    env=dict(os.environ, FIXTURE=directory, FAIL=failure),
                                    capture_output=True, text=True, timeout=10)
            events = (root / "events").read_text().splitlines() if (root / "events").exists() else []
            journal = (root / "journal").read_text() if (root / "journal").exists() else None
            return result, events, journal

    def test_gateway_then_delivery_with_all_proofs(self):
        result, events, journal = self.run_draft()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["POST-gateway", "POST-delivery"], [x for x in events if x.startswith("POST-")])
        self.assertLess(events.index("gateway-proof"), events.index("schema"))
        self.assertLess(events.index("schema"), events.index("POST-delivery"))
        self.assertLess(events.index("gateway-proof"), events.index("POST-delivery"))
        self.assertLess(events.index("wire-proof"), events.index("complete"))
        self.assertEqual("complete", journal)

    def test_missing_or_failed_preflight_never_submits(self):
        for failure in ("missing-adapter", "lock", "authority", "daemon", "credential", "candidate", "journal-begin"):
            with self.subTest(failure=failure):
                result, events, _ = self.run_draft(failure)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(any(x.startswith("POST-") for x in events))

    def test_schema_after_gateway_failure_never_submits_delivery(self):
        result, events, journal = self.run_draft("schema")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(["POST-gateway"], [x for x in events if x.startswith("POST-")])
        self.assertEqual("gateway-verified", journal)

    def test_final_dual_capture_failure_cannot_complete(self):
        result, events, journal = self.run_draft("both-final")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("delivery-verified", journal)
        self.assertNotIn("complete", events)

    def test_uncertain_or_unverified_gateway_never_activates_delivery(self):
        for failure in ("gateway-lost-ack", "gateway-proof", "gateway-verified"):
            result, events, journal = self.run_draft(failure)
            self.assertNotEqual(0, result.returncode)
            self.assertEqual(["POST-gateway"], [x for x in events if x.startswith("POST-")])
            self.assertEqual("gateway-submission-pending", journal)

    def test_delivery_uncertainty_or_proof_failure_never_retries(self):
        for failure in ("delivery-lost-ack", "delivery-proof", "wire-proof"):
            result, events, journal = self.run_draft(failure)
            self.assertNotEqual(0, result.returncode)
            self.assertEqual(["POST-gateway", "POST-delivery"], [x for x in events if x.startswith("POST-")])
            self.assertEqual("delivery-submission-pending", journal)

    def test_existing_history_forbids_reentry(self):
        result, events, journal = self.run_draft(existing=True)
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(any(x.startswith("POST-") for x in events))
        self.assertEqual("gateway-submission-pending", journal)


if __name__ == "__main__":
    unittest.main()
