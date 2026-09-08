#!/usr/bin/env python3
"""Execute the staging retention policy against synthetic Swarm metadata (no daemon)."""
import copy
import json
import os
import re
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/staging-delivery-auth-retention.sh"
NAME = "jeeb-staging-delivery-service-auth-v1"

def specimen(role):
    uid = "65532" if role == "gateway" else "0"
    env = ["DELIVERY_SERVICE_TOKEN_FILE=/run/secrets/delivery_service_token"]
    if role == "delivery":
        env += ["DELIVERY_SERVICE_AUTH_MODE=required", "SKIP_DB_INIT=true"]
    return {"TaskTemplate": {"ContainerSpec": {"Env": env, "Secrets": [{
        "SecretID": "dedicatedid", "SecretName": NAME,
        "File": {"Name": "delivery_service_token", "UID": uid, "GID": uid, "Mode": 256}
    }]}}}

class Retention(unittest.TestCase):
    def run_policy(self, role, before, after=None, metadata=None):
        if metadata is None:
            metadata = [{"ID": "dedicatedid", "Spec": {"Name": NAME, "Labels": {
                "jeeb.environment": "staging", "jeeb.purpose": "delivery-service-auth",
                "jeeb.version": "1"}}}]
        with tempfile.TemporaryDirectory(dir=Path.home()) as temp:
            p = Path(temp)
            (p / "before").write_text(json.dumps(before))
            (p / "after").write_text(json.dumps(after if after is not None else before))
            (p / "metadata").write_text(json.dumps(metadata))
            result = subprocess.run(["bash", "-euc", """
source "$1"
docker() {
  [ "$1" = secret ] && [ "$2" = inspect ] && [ "$#" = 3 ] || return 97
  cat "$AUTH_FIXTURE/metadata"
}
before=$(staging_delivery_auth_snapshot "$2" "$AUTH_FIXTURE/before")
staging_delivery_auth_assert_retained "$before" "$2" "$AUTH_FIXTURE/after"
""", "test", str(SCRIPT), role], env={**os.environ, "AUTH_FIXTURE": temp, "HOME": temp},
                capture_output=True, text=True)
            return result.returncode

    def test_active_and_inactive(self):
        for role in ("gateway", "delivery"):
            self.assertEqual(self.run_policy(role, specimen(role)), 0)
            inactive = {"TaskTemplate": {"ContainerSpec": {"Env": ["SKIP_DB_INIT=true"]}}}
            self.assertEqual(self.run_policy(role, inactive), 0)
        inactive["TaskTemplate"]["ContainerSpec"]["Env"].append("DELIVERY_SERVICE_AUTH_MODE=disabled")
        self.assertNotEqual(self.run_policy("delivery", inactive), 0)

    def test_partial_duplicate_inline_alias_and_mount_conflicts(self):
        for role in ("gateway", "delivery"):
            for mutation in ("missing_file", "missing_mount", "duplicate_file",
                             "duplicate_mount", "inline", "alias", "wrong_path", "bind", "config"):
                with self.subTest(role=role, mutation=mutation):
                    doc = specimen(role)
                    c = doc["TaskTemplate"]["ContainerSpec"]
                    if mutation == "missing_file": c["Env"] = c["Env"][1:]
                    if mutation == "missing_mount": c["Secrets"] = []
                    if mutation == "duplicate_file": c["Env"].append(c["Env"][0])
                    if mutation == "duplicate_mount": c["Secrets"] *= 2
                    if mutation == "inline": c["Env"].append("DELIVERY_SERVICE_TOKEN=conflict")
                    if mutation == "alias": c["Env"].append("Services__Delivery__ServiceTokenFile=/other")
                    if mutation == "wrong_path": c["Env"][0] = "DELIVERY_SERVICE_TOKEN_FILE=/other"
                    if mutation == "bind": c["Mounts"] = [{"Target": "/run/secrets"}]
                    if mutation == "config": c["Configs"] = [{"File": {"Name": "delivery_service_token"}}]
                    self.assertNotEqual(self.run_policy(role, doc), 0)

    def test_exact_mount_metadata(self):
        for role in ("gateway", "delivery"):
            for key, value in (("UID", "123"), ("GID", "123"), ("Mode", 292), ("Name", "other")):
                doc = specimen(role)
                doc["TaskTemplate"]["ContainerSpec"]["Secrets"][0]["File"][key] = value
                self.assertNotEqual(self.run_policy(role, doc), 0)

    def test_secret_identity_and_labels(self):
        for metadata in ([], [{"ID": "wrong"}],
            [{"ID": "dedicatedid", "Spec": {"Name": NAME, "Labels": {}}}],
            [{"ID": "dedicatedid", "Spec": {"Name": NAME, "Labels": {
                "jeeb.environment": "production", "jeeb.purpose": "delivery-service-auth", "jeeb.version": "1"}}}]):
            self.assertNotEqual(self.run_policy("gateway", specimen("gateway"), metadata=metadata), 0)

    def test_delivery_required_mode_and_skip(self):
        for row in ("DELIVERY_SERVICE_AUTH_MODE=disabled", "SKIP_DB_INIT=false"):
            doc = specimen("delivery")
            c = doc["TaskTemplate"]["ContainerSpec"]
            c["Env"] = [x for x in c["Env"] if not x.startswith(row.split("=")[0] + "=")] + [row]
            self.assertNotEqual(self.run_policy("delivery", doc), 0)

    def test_retention_rejects_downgrade_and_replacement(self):
        for role in ("gateway", "delivery"):
            before = specimen(role)
            after = {"TaskTemplate": {"ContainerSpec": {"Env": ["SKIP_DB_INIT=true"]}}}
            self.assertNotEqual(self.run_policy(role, before, after), 0)
            after = copy.deepcopy(before)
            after["TaskTemplate"]["ContainerSpec"]["Secrets"][0]["SecretID"] = "replacementid"
            self.assertNotEqual(self.run_policy(role, before, after), 0)

    def test_unrelated_update_is_allowed(self):
        for role in ("gateway", "delivery"):
            before = specimen(role)
            after = copy.deepcopy(before)
            after["TaskTemplate"]["ContainerSpec"]["Image"] = "repo@sha256:new"
            after["TaskTemplate"]["ContainerSpec"]["Env"].append("PORT=8080")
            self.assertEqual(self.run_policy(role, before, after), 0)

    def test_real_workflow_candidate_filters_retain_auth(self):
        workflow = (ROOT / ".github/workflows/jeeb-staging-deploy.yml").read_text()
        for mode in ("normal", "security-cutover", "otp-cutover", "devtool-reassert"):
            marker = ('--slurpfile secret_additions "$secret_additions_json"' if mode == "devtool-reassert"
                      else '--slurpfile secret_remove "$secret_remove_json"')
            query = workflow.split(marker + " '", 1)[1].split("' \"$current_spec\" > \"$candidate\"", 1)[0]
            removed = re.findall(r"^\s+remove_secret_target ([a-z_]+)$", workflow, re.M)
            stale = workflow.split("for stale_env in ", 1)[1].split("; do", 1)[0]
            env_removed = re.findall(r"[A-Z][A-Z_]{3,}", stale)
            args = ["jq", "--arg", "image", "repo@sha256:new",
                    "--arg", "network_id", "overlayid", "--arg", "deployment_mode", mode]
            for key, value in {"secret_additions": [[]], "secret_remove": [removed],
                               "desired_env": [[]], "env_remove": [env_removed]}.items():
                args += ["--argjson", key, json.dumps(value)]
            result = subprocess.run(args + [query], input=json.dumps(specimen("gateway")),
                                    text=True, capture_output=True, check=True)
            candidate = json.loads(result.stdout)
            self.assertEqual(self.run_policy("gateway", specimen("gateway"), candidate), 0, mode)
            # A removed mounted credential must be rejected by the same executable gate.
            candidate["TaskTemplate"]["ContainerSpec"]["Secrets"] = []
            self.assertNotEqual(self.run_policy("gateway", specimen("gateway"), candidate), 0, mode)

    def test_workflow_wires_fail_closed_checks(self):
        workflow = (ROOT / ".github/workflows/jeeb-staging-deploy.yml").read_text()
        self.assertIn("cat scripts/staging-delivery-auth-retention.sh", workflow)
        self.assertIn("delivery_auth_before=$(staging_delivery_auth_snapshot", workflow)
        self.assertIn('staging_delivery_auth_assert_retained "$delivery_auth_before"', workflow)
        self.assertNotIn("remove_secret_target delivery_service_token", workflow)
        self.assertNotIn("for stale_env in DELIVERY_SERVICE_TOKEN_FILE", workflow)
        if 'cat "$candidate"' in workflow:
            self.assertLess(workflow.index("staging_delivery_auth_assert_retained"),
                            workflow.index('cat "$candidate"'))
        else:
            self.assertLess(workflow.index("delivery_auth_before=$(staging_delivery_auth_snapshot"),
                            workflow.index('docker service update --image'))

if __name__ == "__main__":
    unittest.main()
