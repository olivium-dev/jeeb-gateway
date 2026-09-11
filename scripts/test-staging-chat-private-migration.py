#!/usr/bin/env python3
import copy
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import subprocess
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


m = load("private_migration", "staging-chat-private-migration.py")
c = load("private_custody", "staging-paired-custody.py")
NODE, NETWORK, SECRET = "n" * 25, m.NETWORK_ID, "PRIVATE_CANARY_DO_NOT_EMIT"


def service(role):
    image = m.manifest()["image"] if role == "chat" else "ghcr.io/olivium-dev/jeeb-gateway@sha256:" + "a" * 64
    env = ["ChatServiceApi__BaseUrl=" + m.OLD_URL, "FeatureFlags__UseUpstream__Chat=true"] if role == "gateway" else [
        "Firestore__ProjectId=jeeb-5a293", "Firestore__DatabaseId=(default)",
        "Firestore__KeyFilePath=/run/secrets/jeeb-firebase-adminsdk.json", "Firebase__Chat__IdentityEndpointEnabled=false"]
    env += ["UnrelatedSetting=" + SECRET]
    spec = {"Name": m.SERVICES[role], "Mode": {"Replicated": {"Replicas": 1}},
            "UpdateConfig": {"FailureAction": "pause", "Order": "start-first"},
            "EndpointSpec": {"Mode": "vip", "Ports": copy.deepcopy(m.OLD_PORTS if role == "chat" else m.GATEWAY_PORTS)},
            "TaskTemplate": {"Networks": [{"Target": NETWORK}],
                             "ContainerSpec": {"Image": image, "Env": env, "Secrets": []}}}
    if role == "chat":
        spec["TaskTemplate"]["ContainerSpec"]["Secrets"] = [{"SecretID": "s" * 25, "SecretName": "firebase-fixed",
            "File": {"Name": "jeeb-firebase-adminsdk.json", "UID": "1654", "GID": "1654", "Mode": 0o400}}]
    return {"ID": ("g" if role == "gateway" else "c") * 25, "Version": {"Index": 10},
            "Spec": spec, "Endpoint": copy.deepcopy(spec["EndpointSpec"]), "UpdateStatus": {"State": "completed"}}


def runtime_fixture(role, private=False):
    original = service(role)
    if private:
        original["Spec"] = m.gateway_candidate(original["Spec"]) if role == "gateway" else m.chat_candidate(original["Spec"])
        original["Endpoint"] = copy.deepcopy(original["Spec"]["EndpointSpec"])
    declaration = original["Spec"]["TaskTemplate"]["ContainerSpec"]
    image = declaration["Image"]
    image_id, cid = "sha256:" + "b" * 64, "c" * 64
    config = {"Entrypoint": ["dotnet"], "Cmd": ["service.dll"], "WorkingDir": "/app", "User": "1654", "Env": [],
              "Labels": {"org.opencontainers.image.revision": m.manifest()["sourceCommit"],
                         "jeeb.source.tree": m.manifest()["sourceTree"],
                         "org.opencontainers.image.source": "https://github.com/olivium-dev/chat-service"}}
    container_config = copy.deepcopy(config)
    container_config["Env"] = declaration["Env"]
    task = {"ID": "t" * 25, "NodeID": NODE, "ServiceID": original["ID"],
            "Status": {"State": "running", "ContainerStatus": {"ContainerID": cid}},
            "Spec": {"ContainerSpec": copy.deepcopy(declaration)}, "NetworksAttachments": [{"Network": {"ID": NETWORK}}]}
    container = {"Id": cid, "Image": image_id, "State": {"Running": True}, "Path": "dotnet", "Args": ["service.dll"],
                 "Config": container_config, "HostConfig": {"NetworkMode": "default", "PortBindings": None},
                 "NetworkSettings": {"Networks": {"staging": {"NetworkID": NETWORK}}, "Ports": {"5176/tcp": None}}}
    values = {("service", m.SERVICES[role]): original,
              ("image", image): {"Id": image_id, "RepoDigests": [image], "Config": config},
              ("tasks", original["ID"]): [task], ("container", cid): container,
              ("network", NETWORK): {"Id": NETWORK, "Name": "jeeb-staging-net", "Driver": "overlay", "Scope": "swarm",
                                      "Ingress": False, "Options": {"encrypted": ""}},
              ("secret", "s" * 25): {"ID": "s" * 25, "Spec": {"Name": "firebase-fixed"}}}
    return original, values


class FakeRuntime:
    def __init__(self, journal, fail=None):
        self.journal, self.fail, self.posts = journal, fail, []
        self.manifest = m.manifest()
        self.services = {role: service(role) for role in m.SERVICES}

    def retention(self, exact=False, seal=None):
        if self.fail == "baseline":
            raise ValueError(SECRET)

    def inspect(self, role):
        return copy.deepcopy(self.services[role])

    def verify(self, role, expected, private):
        if self.fail == role + "-verify" and self.posts:
            raise ValueError(SECRET)
        m.require(self.services[role]["Spec"] == expected)
        return {"serviceId": self.services[role]["ID"], "version": self.services[role]["Version"]["Index"],
                "specSha256": m.digest(expected), "overlayId": NETWORK}

    def wait(self, role, expected, private):
        return self.verify(role, expected, private)

    def submit(self, role, original, candidate):
        index = m.PHASES.index(role + "-submission-pending")
        claim = self.journal.path / f"{index:02d}-{role}-submission-pending.json"
        m.require(claim.is_file())
        self.posts.append(role)
        if self.fail == role + "-post":
            raise ConnectionResetError(SECRET)
        self.services[role]["Spec"] = copy.deepcopy(candidate)
        self.services[role]["Version"]["Index"] += 1


class MigrationTests(unittest.TestCase):
    def test_candidates_change_only_the_exact_authorized_field(self):
        gateway = service("gateway")["Spec"]
        changed = m.gateway_candidate(gateway)
        changed["TaskTemplate"]["ContainerSpec"]["Env"] = gateway["TaskTemplate"]["ContainerSpec"]["Env"]
        self.assertEqual(gateway, changed)
        chat = service("chat")["Spec"]
        changed = m.chat_candidate(chat)
        self.assertNotIn("Ports", changed["EndpointSpec"])
        changed["EndpointSpec"]["Ports"] = chat["EndpointSpec"]["Ports"]
        self.assertEqual(chat, changed)

    def test_legacy_gateway_url_missing_external_private_or_alias_is_rejected(self):
        for value in (None, "https://external.invalid", m.PRIVATE_URL, m.OLD_URL + "/"):
            spec = service("gateway")["Spec"]
            env = spec["TaskTemplate"]["ContainerSpec"]["Env"]
            env.pop(0)
            if value is not None:
                env.append("ChatServiceApi__BaseUrl=" + value)
            with self.assertRaises(ValueError):
                m.gateway_candidate(spec)
        spec = service("gateway")["Spec"]
        spec["TaskTemplate"]["ContainerSpec"]["Env"].append("ChatServiceApi:BaseUrl=" + m.OLD_URL)
        with self.assertRaises(ValueError):
            m.gateway_candidate(spec)

    def test_chat_enabled_alias_or_noncanonical_port_shape_is_rejected(self):
        for row in ("Firebase__Chat__IdentityEndpointEnabled=true", "Firebase:Chat:IdentityEndpointEnabled=false",
                    "firebase__chat__identityendpointenabled=false"):
            spec = service("chat")["Spec"]
            spec["TaskTemplate"]["ContainerSpec"]["Env"][-2] = row
            with self.assertRaises(ValueError):
                m.chat_candidate(spec)
        for ports in ([], m.OLD_PORTS * 2, [{**m.OLD_PORTS[0], "PublishMode": "ingress"}], [{**m.OLD_PORTS[0], "PublishedPort": 10029}]):
            spec = service("chat")["Spec"]
            spec["EndpointSpec"]["Ports"] = ports
            with self.assertRaises(ValueError):
                m.chat_candidate(spec)

    def test_exact_unique_firestore_proven_row_only(self):
        row = {"name": "chat-upstream-readiness", "status": "Healthy",
               "description": "chat-service api/Health/firebase passed (Firestore reachable)"}
        m.assert_chat_row({"checks": [row]})
        for rows in ([], [row, row], [{**row, "status": "Degraded"}], [{**row, "description": "Firestore UNVERIFIED"}],
                     [{**row, "description": "anything healthy"}]):
            with self.assertRaises(ValueError):
                m.assert_chat_row({"checks": rows})

    def test_actual_runtime_uid1654_image_and_network_contracts(self):
        for role in m.SERVICES:
            for private in (False, True):
                original, values = runtime_fixture(role, private)
                result = m.verified_runtime(role, original, lambda kind, key: values[(kind, key)], m.manifest(), private)
                self.assertEqual(NETWORK, result["overlayId"])
                self.assertNotIn(SECRET, json.dumps(result))

    def test_private_runtime_rejects_every_publication_or_process_mount_drift(self):
        for failure in ("spec-port", "endpoint-port", "binding", "host-network", "publish-all", "network", "encryption",
                        "uid", "mount-mode", "project", "image", "entrypoint", "args", "effective-env"):
            original, values = runtime_fixture("chat", True)
            declaration = original["Spec"]["TaskTemplate"]["ContainerSpec"]
            container = values[("container", "c" * 64)]
            if failure == "spec-port": original["Spec"]["EndpointSpec"]["Ports"] = m.OLD_PORTS
            if failure == "endpoint-port": original["Endpoint"]["Ports"] = m.OLD_PORTS
            if failure == "binding": container["NetworkSettings"]["Ports"]["5176/tcp"] = [{"HostIp": "0.0.0.0", "HostPort": "10028"}]
            if failure == "host-network": container["HostConfig"]["NetworkMode"] = "host"
            if failure == "publish-all": container["HostConfig"]["PublishAllPorts"] = True
            if failure == "network": container["NetworkSettings"]["Networks"]["other"] = {"NetworkID": "x" * 25}
            if failure == "encryption": values[("network", NETWORK)]["Options"] = {}
            if failure == "uid": container["Config"]["User"] = "0"
            if failure == "mount-mode": declaration["Secrets"][0]["File"]["Mode"] = 0o444
            if failure == "project": container["Config"]["Env"] = ["Firestore__ProjectId=other"]
            if failure == "image": container["Image"] = "sha256:" + "d" * 64
            if failure == "entrypoint": container["Config"]["Entrypoint"] = ["other"]
            if failure == "args": container["Args"] = ["different.dll"]
            if failure == "effective-env": container["Config"]["Env"] = []
            with self.subTest(failure=failure), self.assertRaises((ValueError, KeyError)):
                m.verified_runtime("chat", original, lambda kind, key: values[(kind, key)], m.manifest(), True)

    def test_only_exact_gateway_ingress_network_is_allowed(self):
        for ingress, valid in ((m.INGRESS_ID, True), ("x" * 25, False)):
            original, values = runtime_fixture("gateway", True)
            values[("tasks", original["ID"])][0]["NetworksAttachments"].append({"Network": {"ID": ingress}})
            values[("container", "c" * 64)]["NetworkSettings"]["Networks"]["ingress"] = {"NetworkID": ingress}
            values[("network", ingress)] = {"Id": ingress, "Ingress": True, "Driver": "overlay", "Scope": "swarm"}
            if valid:
                m.verified_runtime("gateway", original, lambda kind, key: values[(kind, key)], m.manifest(), True)
            else:
                with self.assertRaises(ValueError):
                    m.verified_runtime("gateway", original, lambda kind, key: values[(kind, key)], m.manifest(), True)

    def test_false_encryption_duplicate_networks_and_startup_overrides_reject(self):
        for failure in ("false-encryption", "duplicate-task", "duplicate-container", "aspnetcore_environment", "dotnet_environment",
                        "PATH", "DOTNET_ROOT", "LD_LIBRARY_PATH", "DOTNET_ADDITIONAL_DEPS"):
            original, values = runtime_fixture("chat", True)
            declaration = original["Spec"]["TaskTemplate"]["ContainerSpec"]
            task = values[("tasks", original["ID"])][0]
            container = values[("container", "c" * 64)]
            if failure == "false-encryption":
                values[("network", NETWORK)]["Options"]["encrypted"] = "false"
            elif failure == "duplicate-task":
                task["NetworksAttachments"] *= 2
            elif failure == "duplicate-container":
                container["NetworkSettings"]["Networks"]["duplicate"] = {"NetworkID": NETWORK}
            else:
                declaration["Env"].append(failure + "=Production")
                task["Spec"]["ContainerSpec"]["Env"] = copy.deepcopy(declaration["Env"])
                container["Config"]["Env"] = copy.deepcopy(declaration["Env"])
            with self.subTest(failure=failure), self.assertRaises(ValueError):
                m.verified_runtime("chat", original, lambda kind, key: copy.deepcopy(values[(kind, key)]), m.manifest(), True)

    def test_stable_second_reads_ignore_only_health_log_churn(self):
        for change, permitted in (("service", False), ("task", False), ("container", False), ("health-log", True), ("health-status", False)):
            original, values = runtime_fixture("chat", True)
            values[("container", "c" * 64)]["State"]["Health"] = {"Status": "healthy", "Log": ["old"]}
            counts = {}
            def get(kind, key):
                counts[kind] = counts.get(kind, 0) + 1
                value = copy.deepcopy(values[(kind, key)])
                if change == "service" and kind == "service": value["Version"]["Index"] += 1
                if change == "task" and kind == "tasks" and counts[kind] == 2: value[0]["ID"] = "x" * 25
                if kind == "container" and counts[kind] == 2:
                    if change == "container": value["Config"]["User"] = "0"
                    if change == "health-log": value["State"]["Health"]["Log"] = ["new"]
                    if change == "health-status": value["State"]["Health"]["Status"] = "unhealthy"
                return value
            with self.subTest(change=change):
                if permitted:
                    m.verified_runtime("chat", original, get, m.manifest(), True)
                else:
                    with self.assertRaises(ValueError): m.verified_runtime("chat", original, get, m.manifest(), True)

    def test_readiness_polls_only_transient_failures_without_posting_service_updates(self):
        with (patch.object(m, "http_probe", side_effect=[m.ReadinessPending(), ConnectionRefusedError(), None]) as probe,
              patch.object(m.time, "sleep") as sleep):
            m.wait_chat_readiness(Mock())
            self.assertEqual(3, probe.call_count)
            self.assertEqual(2, sleep.call_count)
        with patch.object(m, "http_probe", side_effect=ValueError("wrong proof")) as probe, patch.object(m.time, "sleep") as sleep:
            with self.assertRaises(ValueError): m.wait_chat_readiness(Mock())
            self.assertEqual(1, probe.call_count)
            sleep.assert_not_called()
        with patch.object(m, "http_probe", side_effect=m.ReadinessPending()) as probe, patch.object(m.time, "sleep"):
            with self.assertRaises(m.ReadinessPending): m.wait_chat_readiness(Mock())
            self.assertEqual(30, probe.call_count)
        row = {"name": "chat-upstream-readiness", "status": "Degraded", "description": "disabled by flag"}
        with self.assertRaises(ValueError): m.readiness_result({"checks": [row]})
        for description in ("unknown topology", "UNVERIFIED", "wrong project", "disabled by flag"):
            row.update(status="Unhealthy", description=description)
            with self.assertRaises(ValueError): m.readiness_result({"checks": [row]})
        for description in ("chat-service readiness probe could not be completed",
                            "chat-service readiness probe exceeded the 3s budget",
                            "chat-service api/Health/firebase returned 503"):
            row.update(status="Unhealthy", description=description)
            with self.assertRaises(m.ReadinessPending): m.readiness_result({"checks": [row]})
        verify = Mock(side_effect=[None, ValueError("runtime drift")])
        with patch.object(m, "http_probe", side_effect=m.ReadinessPending()) as probe, patch.object(m.time, "sleep"):
            with self.assertRaises(ValueError): m.wait_chat_readiness(verify)
            self.assertEqual(2, verify.call_count)
            self.assertEqual(1, probe.call_count)

    def test_runtime_guard_failures_do_not_poll_and_all_retention_calls_bind_approved_seal(self):
        baseline = Mock()
        runtime = m.Runtime(baseline, m.manifest(), "a" * 64, Path("/unused"), "b" * 64)
        runtime.retention()
        baseline.verify_retention.assert_called_once_with(Path("/unused"), "a" * 64, exact=False, expected_seal="b" * 64)
        with self.assertRaises(ValueError): runtime.retention(seal="c" * 64)
        original = service("chat")
        with (patch.object(runtime, "inspect", return_value=original), patch.object(runtime, "verify", side_effect=ValueError("topology")) as verify,
              patch.object(m.time, "sleep") as sleep):
            with self.assertRaises(ValueError): runtime.wait("chat", original["Spec"], False)
            self.assertEqual(1, verify.call_count)
            sleep.assert_not_called()

    def test_failing_http_status_does_not_hide_structural_or_unknown_readiness(self):
        for description, error in (
            ("chat-service readiness probe could not be completed", m.ReadinessPending),
            ("wrong project", ValueError),
            ("disabled by flag", ValueError),
        ):
            response = Mock(status=503)
            response.read.return_value = json.dumps({"checks": [{"name": "chat-upstream-readiness",
                "status": "Unhealthy", "description": description}]}).encode()
            connection = Mock()
            connection.getresponse.return_value = response
            with patch.object(m.http.client, "HTTPConnection", return_value=connection), self.assertRaises(error):
                m.http_probe("gateway-chat")
            connection.request.assert_called_once_with("GET", "/health/ready")

    def test_post_cas_same_name_replacement_cannot_change_service_identity(self):
        runtime = m.Runtime(Mock(), m.manifest(), "a" * 64, Path("/unused"), "b" * 64)
        original = service("chat")
        replacement = copy.deepcopy(original)
        replacement["ID"] = "r" * 25
        replacement["Version"]["Index"] = 100
        replacement["Spec"] = m.chat_candidate(original["Spec"])
        with patch.object(m, "engine_get", side_effect=[original, replacement]), patch.object(runtime, "verify") as verify:
            runtime.inspect("chat")
            with self.assertRaises(ValueError): runtime.wait("chat", replacement["Spec"], True)
            verify.assert_not_called()

    def test_claimed_posts_are_one_use_and_uncertain_results_never_retry(self):
        for failure, expected_posts in ((None, ["gateway", "chat"]), ("baseline", []), ("gateway-post", ["gateway"]),
                                        ("gateway-verify", ["gateway"]), ("chat-post", ["gateway", "chat"])):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                home = Path(temporary).resolve()
                (home / ".jeeb-deploy").mkdir(mode=0o700)
                journal = m.Journal(home, c)
                runtime = FakeRuntime(journal, failure)
                with patch.object(m, "http_probe"):
                    if failure:
                        with self.assertRaises((ValueError, ConnectionResetError)):
                            m.migrate(runtime, journal, "a" * 40, "1", "1", "b" * 64)
                    else:
                        result = m.migrate(runtime, journal, "a" * 40, "1", "1", "b" * 64)
                        self.assertEqual("complete", result["migration"])
                        self.assertFalse(result["identityActivationAuthorized"])
                self.assertEqual(expected_posts, runtime.posts)
                if journal.path.exists():
                    before = {path.name: path.read_bytes() for path in journal.path.iterdir()}
                    self.assertNotIn(SECRET, b"".join(before.values()).decode())
                    with self.assertRaises(FileExistsError):
                        journal.begin({})
                    self.assertEqual(before, {path.name: path.read_bytes() for path in journal.path.iterdir()})

    def test_engine_post_preserves_registry_auth_and_checks_incumbent_before_submission(self):
        baseline = Mock()
        runtime = m.Runtime(baseline, m.manifest(), "a" * 64, Path("/unused"), "b" * 64)
        original = service("gateway")
        candidate = m.gateway_candidate(original["Spec"])
        connection = Mock()
        connection.getresponse.return_value.status = 200
        connection.getresponse.return_value.read.return_value = b"{}"
        with patch.object(runtime, "inspect", return_value=original), patch.object(m, "Engine", return_value=connection):
            runtime.submit("gateway", original, candidate)
        args, kwargs = connection.request.call_args
        self.assertEqual("POST", args[0])
        self.assertEqual("/v1.52/services/" + original["ID"] + "/update?version=10&registryAuthFrom=spec", args[1])
        self.assertEqual({"Content-Type": "application/json"}, kwargs["headers"])
        self.assertEqual(candidate, json.loads(kwargs["body"]))
        connection.reset_mock()
        drift = copy.deepcopy(original)
        drift["Version"]["Index"] += 1
        with patch.object(runtime, "inspect", return_value=drift), patch.object(m, "Engine", return_value=connection):
            with self.assertRaises(ValueError): runtime.submit("gateway", original, candidate)
        connection.request.assert_not_called()

    def test_final_receipt_requires_both_fresh_runtime_proofs(self):
        for failing_role in (None, "gateway", "chat"):
            with self.subTest(role=failing_role), tempfile.TemporaryDirectory() as temporary:
                home = Path(temporary).resolve()
                (home / ".jeeb-deploy").mkdir(mode=0o700)
                journal = m.Journal(home, c)
                runtime = FakeRuntime(journal)
                original_verify, final_roles = runtime.verify, []
                def verify(role, expected, private):
                    if (journal.path / "04-chat-verified.json").exists():
                        final_roles.append(role)
                        self.assertTrue(private)
                        if role == failing_role:
                            raise ValueError("final runtime drift")
                    return original_verify(role, expected, private)
                with patch.object(runtime, "verify", side_effect=verify), patch.object(m, "http_probe"):
                    if failing_role:
                        with self.assertRaises(ValueError):
                            m.migrate(runtime, journal, "a" * 40, "1", "1", "b" * 64)
                    else:
                        m.migrate(runtime, journal, "a" * 40, "1", "1", "b" * 64)
                self.assertEqual(["gateway"] if failing_role == "gateway" else ["gateway", "chat"], final_roles)
                self.assertEqual(failing_role is None, (journal.path / "05-complete.json").exists())
                self.assertEqual(["gateway", "chat"], runtime.posts)

    def test_real_bundle_is_self_contained_and_activation_stops_without_runtime(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output): m.bundle()
        source = output.getvalue()
        with tempfile.TemporaryDirectory() as temporary:
            accepted = subprocess.run([os.sys.executable, "-I", "-B", "-", "validate-operation", "migrate-private"],
                                      input=source, text=True, capture_output=True, cwd=temporary, timeout=10)
            self.assertEqual(0, accepted.returncode, accepted.stderr)
            rejected = subprocess.run([os.sys.executable, "-I", "-B", "-", "activate-identity", "a" * 40, "1", "1", "b" * 64],
                                      input=source, text=True, capture_output=True, cwd=temporary, timeout=10)
            self.assertEqual(1, rejected.returncode)
            self.assertNotIn("Traceback", rejected.stderr)
            self.assertNotIn(SECRET, rejected.stderr + rejected.stdout)

    def test_explicit_policy_rejects_added_mutation_or_removed_claim_authority(self):
        policy = load("migration_policy", "check-staging-chat-private-migration.py")
        source = (ROOT / "scripts/staging-chat-private-migration.py").read_text()
        with patch.object(policy.ast, "unparse", side_effect=AssertionError("formatting is not authority")):
            policy.check_source(source)
        for changed in (
            source.replace("&registryAuthFrom=spec", ""),
            source.replace('"/api/firebase/token"', '"/api/firebase/other"'),
            source.replace("/update?version={version}", "/remove?version={version}"),
            source.replace("{original['ID']}/update", "{original['Name']}/update"),
            source.replace('require(operation == "migrate-private")', 'require(True)'),
            source.replace('journal.advance("chat-submission-pending")', 'pass'),
            source.replace('expected_seal=self.approved_seal', 'expected_seal=None'),
            source + '\nconnection.request("POST", "/v1.52/services/create")\n',
        ):
            with self.assertRaises((AssertionError, ValueError)):
                policy.check_source(changed)

    def test_activation_rejected_before_runtime_and_workflow_uses_local_bundle(self):
        with self.assertRaises(ValueError): m.validate_operation("activate-identity")
        workflow = (ROOT / ".github/workflows/jeeb-staging-chat-private-migration.yml").read_text()
        for marker in ("environment: staging", "python3 -I -B -", "staging-paired-source-guard.sh",
                       'validate-operation "$OPERATION"', '"$BASELINE_SEAL"', "persist-credentials: false",
                       'staging-delivery-baseline-provenance.py', '[ "$approved_seal" = "$BASELINE_SEAL" ]'):
            self.assertIn(marker, workflow)
        for forbidden in ("packages: write", "scp ", "docker login", "docker exec", "secrets: inherit"):
            self.assertNotIn(forbidden, workflow)


if __name__ == "__main__":
    unittest.main()
