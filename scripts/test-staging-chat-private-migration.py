#!/usr/bin/env python3
import copy
import contextlib
import fcntl
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
    config = {"Entrypoint": ["dotnet"], "Cmd": ["service.dll"], "WorkingDir": "/app", "User": "1654" if role == "chat" else "appuser", "Env": [],
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

    def test_exact_signing_targets_with_absent_service_users_preserve_spec(self):
        for private in (False, True):
            for name in ("jeeb-firebase-adminsdk.json", "/run/secrets/jeeb-firebase-adminsdk.json"):
                for role in m.SERVICES:
                    original, values = runtime_fixture(role, private)
                    declaration = original["Spec"]["TaskTemplate"]["ContainerSpec"]
                    self.assertNotIn("User", declaration)
                    if role == "chat": declaration["Secrets"][0]["File"]["Name"] = name
                    values[("tasks", original["ID"])][0]["Spec"]["ContainerSpec"] = copy.deepcopy(declaration)
                    self.assertEqual("1654" if role == "chat" else "appuser", values[("container", "c" * 64)]["Config"]["User"])
                    captured = copy.deepcopy(values)
                    with self.subTest(role=role, private=private, name=name):
                        result = m.verified_runtime(role, original, lambda kind, key: values[(kind, key)], m.manifest(), private)
                        self.assertEqual(m.digest(original["Spec"]), result["specSha256"])
                        self.assertEqual(captured, values, "verification must not normalize the captured Spec")

    def test_signing_target_equivalence_keeps_existing_user_guard(self):
        for role in m.SERVICES:
            for user in ("", None, "root", "wrong-user"):
                original, values = runtime_fixture(role, True)
                declaration = original["Spec"]["TaskTemplate"]["ContainerSpec"]
                declaration["User"] = user
                values[("tasks", original["ID"])][0]["Spec"]["ContainerSpec"] = copy.deepcopy(declaration)
                with self.subTest(role=role, user=user), self.assertRaises(ValueError):
                    m.verified_runtime(role, original, lambda kind, key: values[(kind, key)], m.manifest(), True)

    def test_signing_target_equivalence_rejects_other_paths_and_metadata(self):
        cases = [("Name", name) for name in ("/run/secrets/./jeeb-firebase-adminsdk.json", "./jeeb-firebase-adminsdk.json",
                 "/run/secrets//jeeb-firebase-adminsdk.json", "/run/secrets/../secrets/jeeb-firebase-adminsdk.json",
                 "/tmp/jeeb-firebase-adminsdk.json", "/run/secrets/jeeb-firebase-adminsdk.json/", "JEEB-firebase-adminsdk.json")]
        cases += [("UID", "0"), ("UID", "app"), ("GID", "0"), ("GID", "app"), ("Mode", 0o444),
                  ("Mode", "256"), ("Extra", "unreviewed")]
        for field, value in cases:
            original, values = runtime_fixture("chat", True)
            declaration = original["Spec"]["TaskTemplate"]["ContainerSpec"]
            declaration["Secrets"][0]["File"]["Name"] = "/run/secrets/jeeb-firebase-adminsdk.json"
            declaration["Secrets"][0]["File"][field] = value
            values[("tasks", original["ID"])][0]["Spec"]["ContainerSpec"] = copy.deepcopy(declaration)
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                m.verified_runtime("chat", original, lambda kind, key: values[(kind, key)], m.manifest(), True)
        for failure in ("duplicate", "task-spelling", "secret-id", "secret-name"):
            original, values = runtime_fixture("chat", True)
            declaration = original["Spec"]["TaskTemplate"]["ContainerSpec"]
            if failure == "duplicate":
                declaration["Secrets"].append(copy.deepcopy(declaration["Secrets"][0]))
                declaration["Secrets"][1]["File"]["Name"] = "/run/secrets/jeeb-firebase-adminsdk.json"
            task = values[("tasks", original["ID"])][0]
            task["Spec"]["ContainerSpec"] = copy.deepcopy(declaration)
            if failure == "task-spelling": task["Spec"]["ContainerSpec"]["Secrets"][0]["File"]["Name"] = "/run/secrets/jeeb-firebase-adminsdk.json"
            if failure == "secret-id": values[("secret", "s" * 25)]["ID"] = "x" * 25
            if failure == "secret-name": values[("secret", "s" * 25)]["Spec"]["Name"] = "different"
            with self.subTest(failure=failure), self.assertRaises(ValueError):
                m.verified_runtime("chat", original, lambda kind, key: values[(kind, key)], m.manifest(), True)

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
            source.replace('require(operation in ("migrate-private", "diagnose-private"))', 'require(True)'),
            source.replace('def diagnose(baseline, home, approved_seal):',
                           'def diagnose(baseline, home, approved_seal):\n    Journal(home, baseline.c)'),
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


class DiagnosticTests(unittest.TestCase):
    def make_home(self, temporary):
        home = Path(temporary).resolve()
        deploy = home / ".jeeb-deploy"
        deploy.mkdir(mode=0o700)
        (deploy / "paired-releases").mkdir(mode=0o700)
        (deploy / "locks").mkdir(mode=0o700)
        lock = deploy / "locks/jeeb-staging-gateway.lock"
        lock.touch(mode=0o600)
        return home, lock

    def fixture(self):
        gateway, gv = runtime_fixture("gateway", False)
        chat, cv = runtime_fixture("chat", False)
        container = gv.pop(("container", "c" * 64))
        container["Id"] = "d" * 64
        gv[("container", "d" * 64)] = container
        gv[("tasks", gateway["ID"])][0]["Status"]["ContainerStatus"]["ContainerID"] = "d" * 64
        values = {**gv, **cv}
        baseline = Mock(c=c)
        snapshot = {"metadata": {"redactionCanary": SECRET}, "secret": {"ID": "s" * 25},
                    "info": {"ID": "daemon", "Swarm": {"NodeID": NODE}},
                    "services": {"gateway": gateway, "delivery": {"ID": "v" * 25}}}
        baseline.a.collect.return_value = snapshot
        baseline.load_seal.return_value = ({"canary": SECRET}, {"snapshot": copy.deepcopy(snapshot), "originals": {}})
        baseline.digest.return_value = "b" * 64
        baseline.origin.return_value = {}
        baseline.stable_runtime.side_effect = lambda value: copy.deepcopy(value)
        return baseline, values

    def test_existing_lock_is_shared_readonly_and_never_creates_or_overwrites(self):
        with tempfile.TemporaryDirectory() as temporary:
            home, lock = self.make_home(temporary)
            before = lock.stat()
            with m.diagnostic_lock(home, c):
                with open(lock, "rb") as stream:
                    fcntl.flock(stream, fcntl.LOCK_SH | fcntl.LOCK_NB)
                    with self.assertRaises(BlockingIOError):
                        fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
                self.assertFalse(lock.with_suffix(".owner").exists())
            self.assertEqual((before.st_ino, before.st_mtime_ns, before.st_size),
                             (lock.stat().st_ino, lock.stat().st_mtime_ns, lock.stat().st_size))
            with open(lock, "rb") as stream:
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
                with self.assertRaises(BlockingIOError):
                    with m.diagnostic_lock(home, c): pass
            lock.with_suffix(".owner").touch(mode=0o600)
            with self.assertRaises(ValueError):
                with m.diagnostic_lock(home, c): pass
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary).resolve()
            with self.assertRaises(FileNotFoundError):
                with m.diagnostic_lock(home, c): pass
            self.assertEqual([], list(home.iterdir()))

    def test_fixed_journal_missing_prefix_invalid_and_complete_are_readonly(self):
        with tempfile.TemporaryDirectory() as temporary:
            home, _ = self.make_home(temporary)
            self.assertEqual("absent", m.diagnostic_journal(home, c, "b" * 64)[0]["state"])
            journal = m.Journal(home, c)
            journal.begin({"redactionCanary": SECRET, "schemaVersion": 1, "identityActivationAuthorized": False,
                           "currentBaselineSeal": "b" * 64, "sourceCommit": "a" * 40, "runId": "1", "attempt": "1"})
            journal.advance("gateway-submission-pending")
            before = {p.name: p.read_bytes() for p in journal.path.iterdir()}
            report, _ = m.diagnostic_journal(home, c, "b" * 64)
            self.assertEqual("prefix", report["state"])
            self.assertTrue(report["gatewayClaimPresent"])
            self.assertNotIn(SECRET, json.dumps(report))
            self.assertEqual(before, {p.name: p.read_bytes() for p in journal.path.iterdir()})
            for phase in m.PHASES[2:]: journal.advance(phase)
            self.assertEqual("complete", m.diagnostic_journal(home, c, "b" * 64)[0]["state"])
            self.assertEqual("invalid", m.diagnostic_journal(home, c, "f" * 64)[0]["state"])
            with patch.object(c, "read_private", side_effect=ValueError(SECRET)):
                report, _ = m.diagnostic_journal(home, c, "b" * 64)
            self.assertEqual("invalid", report["state"])
            self.assertNotIn(SECRET, json.dumps(report))
            with patch.object(c, "directory", side_effect=PermissionError(SECRET)):
                report, _ = m.diagnostic_journal(home, c, "b" * 64)
            self.assertIsNone(report["gatewayClaimPresent"])
            (journal.path / SECRET).touch()
            report, _ = m.diagnostic_journal(home, c, "b" * 64)
            self.assertEqual("invalid", report["state"])
            self.assertIsNone(report["chatClaimPresent"])
            self.assertNotIn(SECRET, json.dumps(report))

    def test_diagnostic_valid_baseline_without_mutation_or_probe_reachability(self):
        with tempfile.TemporaryDirectory() as temporary:
            home, _ = self.make_home(temporary)
            baseline, values = self.fixture()
            with (patch.object(m, "engine_get", side_effect=lambda kind, value: copy.deepcopy(values[(kind, value)])),
                  patch.object(m, "Journal", side_effect=AssertionError("mutation")),
                  patch.object(m, "Runtime", side_effect=AssertionError("mutation")),
                  patch.object(m, "migrate", side_effect=AssertionError("mutation")),
                  patch.object(m, "http_probe", side_effect=AssertionError("probe")),
                  patch.object(c, "held_lock", side_effect=AssertionError("mutation")),
                  patch.object(c, "write_exclusive", side_effect=AssertionError("mutation"))):
                report = m.diagnose(baseline, home, "b" * 64)
            self.assertTrue(report["readOnly"] and report["snapshotStable"])
            self.assertFalse(report["mutationAuthorized"] or report["retryAuthorized"])
            self.assertTrue(all(check["passed"] for check in report["checks"]), report)
            self.assertNotIn(SECRET, json.dumps(report))
            baseline.verify_retention.assert_not_called()

    def test_guard_details_are_reviewed_source_only_and_checkpoint_detects_drift(self):
        for failure in ("empty-user", "absolute-secret-bad-mode", "both-formats", "host", "checkpoint"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                home, _ = self.make_home(temporary)
                baseline, values = self.fixture()
                chat = values[("service", m.SERVICES["chat"])]
                declaration = chat["Spec"]["TaskTemplate"]["ContainerSpec"]
                if failure in ("empty-user", "both-formats"): declaration["User"] = ""
                if failure in ("absolute-secret-bad-mode", "both-formats"): declaration["Secrets"][0]["File"]["Name"] = "/run/secrets/jeeb-firebase-adminsdk.json"
                if failure == "absolute-secret-bad-mode": declaration["Secrets"][0]["File"]["Mode"] = 0o444
                values[("tasks", chat["ID"])][0]["Spec"]["ContainerSpec"] = copy.deepcopy(declaration)
                if failure == "host": baseline.a.collect.side_effect = ValueError(SECRET)
                if failure == "checkpoint": baseline.stable_runtime.side_effect = ["before", "after"]
                with patch.object(m, "engine_get", side_effect=lambda kind, value: copy.deepcopy(values[(kind, value)])):
                    report = m.diagnose(baseline, home, "b" * 64)
                failed = [check for check in report["checks"] if not check["passed"]]
                self.assertTrue(failed)
                self.assertNotIn(SECRET, json.dumps(report))
                self.assertFalse(report["mutationAuthorized"] or report["retryAuthorized"])
                if failure in ("empty-user", "absolute-secret-bad-mode", "both-formats"):
                    self.assertEqual("verified_runtime", failed[0]["function"])
                    self.assertEqual("staging-chat-private-migration.py", failed[0]["helper"])
                    self.assertIsInstance(failed[0]["line"], int)
                    formats = report["runtimeFormats"]["chat"]
                    self.assertEqual(failure in ("empty-user", "both-formats"), formats["serviceUserExplicitEmpty"])
                    self.assertEqual(failure in ("absolute-secret-bad-mode", "both-formats"), formats["signingTargetAbsolute"])
                if failure == "host":
                    baseline.load_seal.assert_not_called()
                    self.assertNotIn("journal", report)
                if failure == "checkpoint": self.assertFalse(report["snapshotStable"])

    def test_diagnostic_accepts_exact_absolute_secret_with_safe_metadata_and_absent_user(self):
        with tempfile.TemporaryDirectory() as temporary:
            home, _ = self.make_home(temporary)
            baseline, values = self.fixture()
            chat = values[("service", m.SERVICES["chat"])]
            declaration = chat["Spec"]["TaskTemplate"]["ContainerSpec"]
            self.assertNotIn("User", declaration)
            declaration["Secrets"][0]["File"]["Name"] = "/run/secrets/jeeb-firebase-adminsdk.json"
            values[("tasks", chat["ID"])][0]["Spec"]["ContainerSpec"] = copy.deepcopy(declaration)
            captured = copy.deepcopy(values)
            with patch.object(m, "engine_get", side_effect=lambda kind, value: copy.deepcopy(values[(kind, value)])):
                report = m.diagnose(baseline, home, "b" * 64)
            self.assertTrue(all(check["passed"] for check in report["checks"]), report)
            self.assertTrue(report["snapshotStable"] and report["readOnly"])
            self.assertTrue(report["runtimeFormats"]["chat"]["signingTargetAbsolute"])
            self.assertFalse(report["runtimeFormats"]["chat"]["serviceUserExplicitEmpty"])
            self.assertFalse(report["mutationAuthorized"] or report["retryAuthorized"])
            self.assertNotIn(SECRET, json.dumps(report))
            self.assertEqual(captured, values)

    def test_main_diagnostic_reports_validated_run_without_constructing_mutation_authority(self):
        baseline = Mock()
        result = {"readOnly": True, "mutationAuthorized": False, "retryAuthorized": False}
        output = io.StringIO()
        with (patch.object(m.sys, "argv", ["helper", "diagnose-private", "a" * 40, "123", "2", "b" * 64]),
              patch.dict(m.sys.modules, {"migration_baseline": baseline}), patch.object(m, "diagnose", return_value=result),
              patch.object(m, "Runtime", side_effect=AssertionError("mutation")),
              patch.object(m, "Journal", side_effect=AssertionError("mutation")),
              patch.object(m, "migrate", side_effect=AssertionError("mutation")), contextlib.redirect_stdout(output)):
            self.assertEqual(0, m.main())
        report = json.loads(output.getvalue())
        self.assertEqual(("a" * 40, "123", "2"), (report["sourceCommit"], report["runId"], report["attempt"]))
        baseline.c.held_lock.assert_not_called()


class MainFailureTests(unittest.TestCase):
    ARGS = ["helper", "migrate-private", "a" * 40, "123", "2", "b" * 64]
    WARNING = "Private chat operation stopped. Any submission claim is consumed; reconcile before further action. Identity activation remains unauthorized."

    def assert_report(self, output, error, source=True):
        self.assertEqual(self.WARNING + "\n", error.getvalue())
        self.assertNotIn(SECRET, output.getvalue() + error.getvalue())
        report = json.loads(output.getvalue())
        self.assertEqual("stopped", report["status"])
        for field in ("mutationAuthorized", "retryAuthorized", "continuationAuthorized", "identityActivationAuthorized"):
            self.assertIs(report[field], False)
        if source:
            self.assertEqual(("a" * 40, "123", "2"), (report["sourceCommit"], report["runId"], report["attempt"]))
        else:
            self.assertTrue({"sourceCommit", "runId", "attempt"}.isdisjoint(report))
        self.assertFalse(report["failure"]["passed"])
        return report

    def test_validated_source_and_safe_real_helper_location_without_private_locals(self):
        baseline = Mock()
        baseline.c.held_lock.return_value = contextlib.nullcontext()
        def fail(*_):
            private_spec = {"Name": SECRET, "unrelated": SECRET}
            m.gateway_candidate(private_spec)
        with (patch.object(m.sys, "argv", self.ARGS), patch.dict(m.sys.modules, {"migration_baseline": baseline}),
              patch.object(m, "Runtime"), patch.object(m, "Journal"), patch.object(m, "migrate", side_effect=fail) as migrate,
              contextlib.redirect_stdout(io.StringIO()) as output, contextlib.redirect_stderr(io.StringIO()) as error):
            self.assertEqual(1, m.main())
        report = self.assert_report(output, error)
        location = report["failure"]
        self.assertEqual("staging-chat-private-migration.py", location["helper"])
        self.assertEqual("gateway_candidate", location["function"])
        self.assertIsInstance(location["line"], int)
        line = (ROOT / "scripts/staging-chat-private-migration.py").read_text().splitlines()[location["line"] - 1]
        self.assertIn('require(spec["Name"] == SERVICES["gateway"])', line)
        migrate.assert_called_once()

    def test_unvalidated_arguments_never_gain_source_binding_or_reach_mutation(self):
        variants = [["helper"], ["helper", "validate-operation", SECRET]]
        for index in range(1, len(self.ARGS)):
            args = self.ARGS.copy()
            args[index] = SECRET
            variants.append(args)
        for args in variants:
            with (self.subTest(args=args), patch.object(m.sys, "argv", args),
                  patch.object(m, "Runtime") as runtime, patch.object(m, "Journal") as journal,
                  patch.object(m, "migrate") as migrate,
                  contextlib.redirect_stdout(io.StringIO()) as output, contextlib.redirect_stderr(io.StringIO()) as error):
                self.assertEqual(1, m.main())
            self.assert_report(output, error, source=False)
            runtime.assert_not_called()
            journal.assert_not_called()
            migrate.assert_not_called()

    def test_failed_claimed_posts_keep_exit_one_and_exact_existing_claims_without_retry(self):
        for failure, expected in (("gateway-post", ["gateway"]), ("chat-post", ["gateway", "chat"])):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                home = Path(temporary).resolve()
                (home / ".jeeb-deploy").mkdir(mode=0o700)
                journal = m.Journal(home, c)
                runtime = FakeRuntime(journal, failure)
                submitted, at_failure = runtime.submit, {}
                def submit(*args):
                    try:
                        submitted(*args)
                    except Exception:
                        at_failure.update({path.name: path.read_bytes() for path in journal.path.iterdir()})
                        raise
                runtime.submit = submit
                baseline = Mock()
                baseline.c.held_lock.return_value = contextlib.nullcontext()
                with (patch.object(m.sys, "argv", self.ARGS), patch.dict(m.sys.modules, {"migration_baseline": baseline}),
                      patch.object(m.Path, "home", return_value=home), patch.object(m, "Runtime", return_value=runtime),
                      patch.object(m, "Journal", return_value=journal), patch.object(m, "http_probe"),
                      contextlib.redirect_stdout(io.StringIO()) as output, contextlib.redirect_stderr(io.StringIO()) as error):
                    self.assertEqual(1, m.main())
                self.assert_report(output, error)
                self.assertEqual(expected, runtime.posts)
                self.assertTrue(at_failure)
                self.assertEqual(at_failure, {path.name: path.read_bytes() for path in journal.path.iterdir()})
                self.assertFalse((journal.path / "05-complete.json").exists())


if __name__ == "__main__":
    unittest.main()
