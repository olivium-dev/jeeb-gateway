"""Real shell history guard tests; synthetic metadata, no Docker daemon."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("retention", Path(__file__).with_name("test-staging-delivery-auth-retention.py"))
retention = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(retention)
PHASES = ("prepared", "gateway-submission-pending", "gateway-verified", "delivery-submission-pending", "delivery-verified", "complete")
SECRET = "a" * 25


def metadata():
    result = {"secretId": SECRET, "receiptNonce": "b" * 64,
              "gatewayBuildRun": "8", "gatewayBuildAttempt": "1"}
    for role, repo in (("gateway", "jeeb-gateway"), ("delivery", "delivery-service")):
        result.update({role + "Source": "c" * 40, role + "Tree": "d" * 40,
                       role + "Image": "ghcr.io/olivium-dev/" + repo + "@sha256:" + "e" * 64,
                       role + "Run": "9", role + "Attempt": "1",
                       role + "ServiceId": "f" * 25, role + "Version": 7})
    return result


class HistoryTests(unittest.TestCase):
    def invoke(self, scenario, role="delivery"):
        # HOME ancestors must be non-writable; /tmp deliberately is not suitable.
        with tempfile.TemporaryDirectory(prefix="paired-history-fixture-", dir=Path.home()) as directory:
            home = Path(directory)
            path = home / ".jeeb-deploy" / "paired-releases" / retention.NAME
            current = retention.specimen(role)
            peer = retention.specimen("gateway" if role == "delivery" else "delivery")
            for spec in (current, peer):
                spec["TaskTemplate"]["ContainerSpec"]["Secrets"][0]["SecretID"] = SECRET
            if scenario != "absent":
                path.mkdir(parents=True, mode=0o700)
                path.parent.chmod(0o700)
                path.parent.parent.chmod(0o700)
                for i, phase in enumerate(PHASES):
                    if scenario == "pending" and i > 1:
                        break
                    value = {"phase": phase, **metadata()}
                    if scenario == "metadata" and i == 5:
                        value["gatewaySource"] = "0" * 40
                    if scenario == "missing_metadata":
                        del value["gatewayTree"]
                    file = path / f"{i:02d}-{phase}.json"
                    file.write_text(json.dumps(value))
                    file.chmod(0o400)
                first = path / "00-prepared.json"
                if scenario == "malformed":
                    first.chmod(0o600)
                    first.write_text("{")
                    first.chmod(0o400)
                if scenario == "unreadable":
                    first.chmod(0)
                if scenario == "symlink":
                    first.rename(home / "outside")
                    first.symlink_to(home / "outside")
                if scenario == "directory_symlink":
                    path.rename(home / "outside")
                    path.symlink_to(home / "outside", target_is_directory=True)
            if scenario in ("absent", "stripped"):
                current = {"TaskTemplate": {"ContainerSpec": {"Env": ["SKIP_DB_INIT=true"]}}}
            if scenario == "peer_stripped":
                peer = {"TaskTemplate": {"ContainerSpec": {"Env": []}}}
            if scenario == "wrong_id":
                current["TaskTemplate"]["ContainerSpec"]["Secrets"][0]["SecretID"] = "z" * 25
            if scenario == "wrong_mode":
                current["TaskTemplate"]["ContainerSpec"]["Secrets"][0]["File"]["Mode"] = 292
            labels = {"jeeb.environment": "staging", "jeeb.purpose": "delivery-service-auth", "jeeb.version": "1"}
            if scenario == "wrong_labels":
                labels["jeeb.version"] = "2"
            for name, value in (("current", current), ("peer", peer), ("secret", [
                    {"ID": SECRET, "Spec": {"Name": retention.NAME, "Labels": labels}}])):
                (home / name).write_text(json.dumps(value))
            result = subprocess.run(["bash", "-euc", '''
source "$1"
docker() {
  case "$1 $2" in
    "secret inspect") cat "$HOME/secret" ;;
    "service inspect") cat "$HOME/peer" ;;
    *) return 98 ;;
  esac
}
staging_delivery_auth_snapshot "$2" "$HOME/current"
''', "fixture", str(retention.SCRIPT), role], env={**os.environ, "HOME": directory},
                capture_output=True, text=True, timeout=5)
            return result

    def test_absent_pre_activation_and_exact_complete_pair_allowed(self):
        for role in ("gateway", "delivery"):
            for scenario in ("absent", "complete"):
                result = self.invoke(scenario, role)
                self.assertEqual(0, result.returncode, result.stderr)

    def test_incomplete_corrupt_or_changed_activated_state_blocks(self):
        for scenario in ("pending", "malformed", "metadata", "missing_metadata", "unreadable",
                         "symlink", "directory_symlink", "stripped", "peer_stripped",
                         "wrong_id", "wrong_mode", "wrong_labels"):
            with self.subTest(scenario=scenario):
                self.assertNotEqual(0, self.invoke(scenario).returncode)


if __name__ == "__main__":
    unittest.main()
