#!/usr/bin/env python3
"""One-use, full-Spec staging chat migration; identity activation stays disabled.

Every Engine submission has an exclusive durable claim before the POST. No retry,
reset, historical journal completion, secret reading, or container exec is offered.
"""
import copy
import contextlib
import fcntl
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import secrets
import socket
import stat
import sys
import time
from urllib.parse import quote

SERVICES = {"gateway": "jeeb-staging-jeeb-gateway", "chat": "jeeb-staging-chat-api"}
OLD_URL = "http://192.168.2.20:10028"
PRIVATE_URL = "http://jeeb-staging-chat-api:5176"
OLD_PORTS = [{"Protocol": "tcp", "TargetPort": 5176, "PublishedPort": 10028, "PublishMode": "host"}]
INGRESS_ID = "xlfdtqi1icuf1ecn12nvaqdqf"
NETWORK_ID = "m4zioz2el3azd5mhj4q6pm9po"
GATEWAY_PORTS = [{"Protocol": "tcp", "TargetPort": 8080, "PublishedPort": 10000, "PublishMode": "ingress"}]
JOURNAL_NAME = "chat-private-migration-v1"
PHASES = ("prepared", "gateway-submission-pending", "gateway-verified", "chat-submission-pending", "chat-verified", "complete")
LIMIT = 1024 * 1024
sys.dont_write_bytecode = True


def require(value):
    if not value:
        raise ValueError("private chat migration guard")


class ReadinessPending(Exception):
    pass


def encoded(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()


def digest(value):
    return hashlib.sha256(encoded(value)).hexdigest()


def identifier(value, pattern=r"[a-z0-9]{25}"):
    require(isinstance(value, str) and re.fullmatch(pattern, value))
    return value


def env_map(rows):
    require(isinstance(rows, list))
    result, normalized = {}, set()
    for row in rows:
        require(isinstance(row, str) and "=" in row)
        key, value = row.split("=", 1)
        canonical = key.replace(":", "__").lower()
        require(key and canonical not in normalized)
        normalized.add(canonical)
        result[key] = value
    return result


def setting(values, canonical, expected, optional=False):
    rows = [(key, value) for key, value in values.items()
            if key.replace(":", "__").lower() == canonical.lower()]
    require((optional and not rows) or rows == [(canonical, expected)])


def disabled_identity(spec):
    declaration = spec["TaskTemplate"]["ContainerSpec"]
    setting(env_map(declaration.get("Env", [])), "Firebase__Chat__IdentityEndpointEnabled", "false", optional=True)


def gateway_candidate(spec):
    require(spec["Name"] == SERVICES["gateway"])
    values = env_map(spec["TaskTemplate"]["ContainerSpec"]["Env"])
    setting(values, "ChatServiceApi__BaseUrl", OLD_URL)
    setting(values, "FeatureFlags__UseUpstream__Chat", "true")
    result = copy.deepcopy(spec)
    result["TaskTemplate"]["ContainerSpec"]["Env"] = [
        "ChatServiceApi__BaseUrl=" + PRIVATE_URL if row.startswith("ChatServiceApi__BaseUrl=") else row
        for row in spec["TaskTemplate"]["ContainerSpec"]["Env"]]
    return result


def chat_candidate(spec):
    require(spec["Name"] == SERVICES["chat"])
    require(spec["EndpointSpec"]["Ports"] == OLD_PORTS)
    disabled_identity(spec)
    result = copy.deepcopy(spec)
    del result["EndpointSpec"]["Ports"]
    return result


def assert_chat_row(body):
    rows = [row for row in body["checks"] if row.get("name") == "chat-upstream-readiness"]
    require(len(rows) == 1 and rows[0].get("status") == "Healthy" and rows[0].get("description") ==
            "chat-service api/Health/firebase passed (Firestore reachable)")


def readiness_result(body):
    require(isinstance(body, dict) and isinstance(body.get("checks"), list))
    require(all(isinstance(row, dict) for row in body["checks"]))
    rows = [row for row in body["checks"] if row.get("name") == "chat-upstream-readiness"]
    require(len(rows) == 1)
    if rows[0].get("status") == "Unhealthy" and rows[0].get("description") in (
            "chat-service readiness probe could not be completed",
            "chat-service readiness probe exceeded the 3s budget",
            "chat-service api/Health/firebase returned 503"):
        raise ReadinessPending()
    assert_chat_row(body)


class Engine(http.client.HTTPConnection):
    def connect(self):
        require(stat.S_ISSOCK(os.lstat("/var/run/docker.sock").st_mode))
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect("/var/run/docker.sock")


def engine_get(kind, value):
    if kind == "service":
        require(value in SERVICES.values())
        path = "/v1.52/services/" + value
    elif kind == "tasks":
        identifier(value)
        path = "/v1.52/tasks?filters=" + quote(json.dumps({"service": [value], "desired-state": ["running"]}))
    else:
        patterns = {"container": r"[0-9a-f]{64}", "network": r"[a-z0-9]{25}", "secret": r"[a-z0-9]{25}",
                    "image": r"ghcr\.io/olivium-dev/(?:jeeb-gateway|chat-service)@sha256:[0-9a-f]{64}"}
        require(kind in patterns)
        identifier(value, patterns[kind])
        path = "/v1.52/" + {"container": "containers", "image": "images", "network": "networks", "secret": "secrets"}[kind] + "/" + quote(value, safe="")
        if kind in ("container", "image"):
            path += "/json"
    connection = Engine("localhost", timeout=10)
    try:
        connection.request("GET", path)
        response = connection.getresponse()
        raw = response.read(LIMIT + 1)
        require(response.status == 200 and len(raw) <= LIMIT)
        return json.loads(raw)
    finally:
        connection.close()


def http_probe(kind):
    require(kind in ("gateway-chat", "disabled-chat"))
    connection = http.client.HTTPConnection("127.0.0.1", 10000 if kind == "gateway-chat" else 10028, timeout=10)
    try:
        if kind == "gateway-chat":
            connection.request("GET", "/health/ready")
        else:
            # Invalid JSON has no UID and cannot mint; the disabled filter must
            # answer before model binding. No credentials or arbitrary headers.
            connection.request("POST", "/api/firebase/token", body=b"{", headers={"Content-Type": "application/json"})
        response = connection.getresponse()
        raw = response.read(LIMIT + 1)
        require(len(raw) <= LIMIT)
        if kind == "gateway-chat" and response.status in (502, 503, 504):
            if raw.strip():
                readiness_result(json.loads(raw))
                # A healthy row with a failing HTTP status is not an expected
                # chat startup failure; unrelated failures are not retried.
                require(False)
            raise ReadinessPending()
        require(response.status == (200 if kind == "gateway-chat" else 404))
        if kind == "gateway-chat":
            readiness_result(json.loads(raw))
    finally:
        connection.close()


def wait_chat_readiness(verify):
    for attempt in range(30):
        # Never turn a topology/custody change into a transient HTTP retry.
        verify()
        try:
            http_probe("gateway-chat")
            return
        except (ReadinessPending, ConnectionRefusedError, TimeoutError):
            if attempt == 29:
                raise
            time.sleep(2)


def stable_container(container):
    value = copy.deepcopy(container)
    value.get("State", {}).get("Health", {}).pop("Log", None)
    return value


class Journal:
    def __init__(self, home, custody):
        self.c = custody
        self.root = custody.custody_root(home)
        self.path = self.root / JOURNAL_NAME

    def begin(self, metadata):
        with self.c.open_directory(self.root) as parent:
            os.mkdir(JOURNAL_NAME, 0o700, dir_fd=parent)
            os.fsync(parent)
        self.c.write_exclusive(self.path / "00-prepared.json", {"phase": "prepared", "metadata": metadata})

    def advance(self, phase, evidence=None):
        require(phase in PHASES[1:])
        index = PHASES.index(phase)
        self.c.directory(self.path)
        with self.c.open_directory(self.path) as parent:
            require(set(os.listdir(parent)) == {f"{number:02d}-{name}.json" for number, name in enumerate(PHASES[:index])})
        previous = self.c.read_private(self.path / f"{index - 1:02d}-{PHASES[index - 1]}.json", 0o400)
        require(previous["phase"] == PHASES[index - 1])
        value = {"phase": phase, "metadata": previous["metadata"], "previousSha256": digest(previous), "evidence": evidence or {}}
        self.c.write_exclusive(self.path / f"{index:02d}-{phase}.json", value)
        return value


def verified_runtime(role, service, get, manifest, private=False):
    spec = service["Spec"]
    require(spec["Name"] == SERVICES[role] and spec["Mode"] == {"Replicated": {"Replicas": 1}})
    require(service.get("UpdateStatus", {}).get("State") == "completed")
    require(spec.get("UpdateConfig", {}).get("FailureAction") == "pause")
    identifier(service["ID"])
    require(type(service["Version"]["Index"]) is int and service["Version"]["Index"] > 0)
    declaration = spec["TaskTemplate"]["ContainerSpec"]
    require(not any(declaration.get(key) for key in ("Command", "Args", "Dir", "Mounts", "Configs")))
    image = declaration["Image"]
    identifier(image, r"ghcr\.io/olivium-dev/" + ("chat-service" if role == "chat" else "jeeb-gateway") + r"@sha256:[0-9a-f]{64}")
    metadata = get("image", image)
    image_config = metadata["Config"]
    require(image in metadata["RepoDigests"])
    tasks = get("tasks", service["ID"])
    require(len(tasks) == 1)
    task = tasks[0]
    require(task["ServiceID"] == service["ID"] and task["Status"]["State"] == "running")
    task_declaration = task["Spec"]["ContainerSpec"]
    require(task_declaration["Image"] == image)
    for key in ("Env", "Command", "Args", "Mounts", "Configs", "Secrets"):
        require((task_declaration.get(key) or []) == (declaration.get(key) or []))
    for key in ("Dir", "User"):
        require((task_declaration.get(key) or "") == (declaration.get(key) or ""))
    container = get("container", identifier(task["Status"]["ContainerStatus"]["ContainerID"], r"[0-9a-f]{64}"))
    require(container["Id"] == task["Status"]["ContainerStatus"]["ContainerID"] and container["State"]["Running"] is True)
    require(container["Image"] == metadata["Id"])
    config = container["Config"]
    for field in ("Entrypoint", "Cmd"):
        require((config.get(field) or []) == (image_config.get(field) or []))
    require(config.get("WorkingDir", "") == image_config.get("WorkingDir", ""))
    command = (image_config.get("Entrypoint") or []) + (image_config.get("Cmd") or [])
    require(command and container["Path"] == command[0] and container["Args"] == command[1:])
    image_env, spec_env, effective = (env_map(item.get("Env", [])) for item in (image_config, declaration, config))
    require(effective == {**image_env, **spec_env})
    env_map([key + "=" + value for key, value in effective.items()])
    require(config.get("User", "") == image_config.get("User", ""))
    require(declaration.get("User", image_config.get("User", "")) == image_config.get("User", ""))
    for mount in container.get("Mounts", []):
        require(isinstance(mount.get("Destination"), str) and mount["Destination"].startswith("/run/secrets/")
                and mount.get("RW") is False)
    for values in (image_env, spec_env, effective):
        for key in ("DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT"):
            require(not any(name.upper() == key and name != key for name in values))
            require(values.get(key, "Production") in ("Production", "Staging"))
    require(not any(key.upper() in {"PATH", "DOTNET_ROOT", "LD_PRELOAD", "LD_LIBRARY_PATH", "DOTNET_ADDITIONAL_DEPS",
                                    "DOTNET_STARTUP_HOOKS", "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"} for key in spec_env))
    require(not any(key.upper() in {"LD_PRELOAD", "LD_LIBRARY_PATH", "DOTNET_ADDITIONAL_DEPS", "DOTNET_STARTUP_HOOKS",
                                   "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"} and value for key, value in effective.items()))
    attachments = spec["TaskTemplate"]["Networks"]
    require(len(attachments) == 1)
    overlay_id = identifier(attachments[0]["Target"])
    require(overlay_id == NETWORK_ID)
    overlay = get("network", overlay_id)
    require(overlay["Id"] == overlay_id and overlay["Name"] == "jeeb-staging-net" and overlay["Driver"] == "overlay"
            and overlay["Scope"] == "swarm" and overlay.get("Ingress") is False and overlay.get("Options", {}).get("encrypted") in ("", "true"))
    runtime_list = [identifier(binding["NetworkID"]) for binding in container["NetworkSettings"]["Networks"].values()]
    task_list = [identifier(binding["Network"]["ID"]) for binding in task["NetworksAttachments"]]
    runtime_ids, task_ids = set(runtime_list), set(task_list)
    require(len(runtime_list) == len(runtime_ids) and len(task_list) == len(task_ids))
    require(runtime_ids == task_ids and overlay_id in runtime_ids)
    extra = runtime_ids - {overlay_id}
    if extra:
        require(role == "gateway" and extra == {INGRESS_ID})
        ingress = get("network", INGRESS_ID)
        require(ingress["Id"] == INGRESS_ID and ingress["Ingress"] is True and ingress["Driver"] == "overlay" and ingress["Scope"] == "swarm")
    if role == "gateway":
        require(spec["EndpointSpec"].get("Ports") == GATEWAY_PORTS and service.get("Endpoint", {}).get("Ports") == GATEWAY_PORTS)
        require(container.get("HostConfig", {}).get("NetworkMode") != "host")
        require(not container.get("HostConfig", {}).get("PublishAllPorts"))
        require(not any((container.get("HostConfig", {}).get("PortBindings") or {}).values()))
        require(not any((container["NetworkSettings"].get("Ports") or {}).values()))
        setting(effective, "ChatServiceApi__BaseUrl", PRIVATE_URL if private else OLD_URL)
        setting(effective, "FeatureFlags__UseUpstream__Chat", "true")
    else:
        require(image == manifest["image"])
        labels = image_config.get("Labels", {})
        require(labels.get("org.opencontainers.image.revision") == manifest["sourceCommit"] and
                labels.get("jeeb.source.tree") == manifest["sourceTree"] and
                labels.get("org.opencontainers.image.source") == "https://github.com/olivium-dev/chat-service")
        disabled_identity(spec)
        setting(effective, "Firebase__Chat__IdentityEndpointEnabled", "false", optional=True)
        for key, value in {"Firestore__ProjectId": "jeeb-5a293", "Firestore__DatabaseId": "(default)",
                           "Firestore__KeyFilePath": "/run/secrets/jeeb-firebase-adminsdk.json"}.items():
            setting(effective, key, value)
        for key in ("Firebase__Chat__ServiceAccountKeyPath", "GOOGLE_APPLICATION_CREDENTIALS"):
            setting(effective, key, "/run/secrets/jeeb-firebase-adminsdk.json", optional=True)
        uid = identifier(image_config["User"], r"[1-9][0-9]*(?::[1-9][0-9]*)?")
        uid, _, gid = uid.partition(":")
        gid = gid or uid
        mounts = [binding for binding in declaration.get("Secrets", []) if binding.get("File", {}).get("Name") in
                  ("jeeb-firebase-adminsdk.json", "/run/secrets/jeeb-firebase-adminsdk.json")]
        require(len(mounts) == 1)
        require(mounts[0]["File"] == {"Name": "jeeb-firebase-adminsdk.json", "UID": uid, "GID": gid, "Mode": 0o400})
        secret = get("secret", identifier(mounts[0]["SecretID"]))
        require(secret["ID"] == mounts[0]["SecretID"] and secret["Spec"]["Name"] == mounts[0]["SecretName"])
        require(task["Spec"]["ContainerSpec"]["Secrets"] == declaration["Secrets"])
        ports = spec["EndpointSpec"].get("Ports", [])
        require(ports == ([] if private else OLD_PORTS) and service.get("Endpoint", {}).get("Ports", []) == ports)
        if private:
            require(container.get("HostConfig", {}).get("NetworkMode") != "host")
            require(not container.get("HostConfig", {}).get("PublishAllPorts"))
            require(not any((container.get("HostConfig", {}).get("PortBindings") or {}).values()))
            require(not any((container["NetworkSettings"].get("Ports") or {}).values()))
    require(get("service", SERVICES[role]) == service)
    require(get("tasks", service["ID"]) == tasks)
    require(stable_container(get("container", container["Id"])) == stable_container(container))
    return {"serviceId": service["ID"], "version": service["Version"]["Index"], "specSha256": digest(spec),
            "image": image, "imageId": metadata["Id"], "taskId": identifier(task["ID"]),
            "nodeId": identifier(task["NodeID"]), "overlayId": overlay_id}


class Runtime:
    def __init__(self, baseline, manifest, owner, home, approved_seal):
        self.baseline, self.manifest, self.owner, self.home = baseline, manifest, owner, home
        self.approved_seal = identifier(approved_seal, r"[0-9a-f]{64}")
        self.service_ids = {}

    def retention(self, exact=False, seal=None):
        require(seal is None or seal == self.approved_seal)
        return self.baseline.verify_retention(self.home, self.owner, exact=exact, expected_seal=self.approved_seal)

    def inspect(self, role):
        current = engine_get("service", SERVICES[role])
        service_id = identifier(current["ID"])
        require(service_id == self.service_ids.setdefault(role, service_id))
        return current

    def verify(self, role, expected, private):
        self.retention()
        current = self.inspect(role)
        require(current["Spec"] == expected)
        result = verified_runtime(role, current, engine_get, self.manifest, private)
        _, evidence = self.baseline.load_seal(self.home)
        require(result["nodeId"] == evidence["snapshot"]["info"]["Swarm"]["NodeID"])
        return result

    def wait(self, role, expected, private):
        for attempt in range(40):
            current = self.inspect(role)
            require(current["Spec"] == expected)
            if current.get("UpdateStatus", {}).get("State") != "updating":
                return self.verify(role, expected, private)
            require(attempt < 39)
            time.sleep(2)

    def submit(self, role, original, candidate):
        self.retention()
        require(self.inspect(role) == original)
        identifier(original["ID"])
        version = original["Version"]["Index"]
        require(type(version) is int and version > 0)
        require(candidate == (gateway_candidate(original["Spec"]) if role == "gateway" else chat_candidate(original["Spec"])))
        connection = Engine("localhost", timeout=30)
        try:
            # Explicit registryAuthFrom=spec retains incumbent image pull custody;
            # neither the image nor any registry credential is changed.
            connection.request("POST", f"/v1.52/services/{original['ID']}/update?version={version}&registryAuthFrom=spec",
                               body=encoded(candidate), headers={"Content-Type": "application/json"})
            response = connection.getresponse()
            raw = response.read(LIMIT + 1)
            require(response.status == 200 and len(raw) <= LIMIT)
            value = json.loads(raw)
            require(isinstance(value, dict) and not value.get("Warnings"))
        finally:
            connection.close()


def migrate(runtime, journal, source, run, attempt, seal):
    runtime.retention(exact=True, seal=seal)
    original = {role: runtime.inspect(role) for role in SERVICES}
    candidate = {"gateway": gateway_candidate(original["gateway"]["Spec"]), "chat": chat_candidate(original["chat"]["Spec"])}
    initial = {role: runtime.verify(role, original[role]["Spec"], False) for role in SERVICES}
    require(initial["chat"]["overlayId"] == initial["gateway"]["overlayId"])
    http_probe("disabled-chat")
    def verify_both(gateway_spec, chat_spec, gateway_private, chat_private):
        runtime.verify("gateway", gateway_spec, gateway_private)
        runtime.verify("chat", chat_spec, chat_private)
    wait_chat_readiness(lambda: verify_both(original["gateway"]["Spec"], original["chat"]["Spec"], False, False))
    metadata = {"schemaVersion": 1, "sourceCommit": source, "runId": run, "attempt": attempt,
                "currentBaselineSeal": seal, "chatBuild": runtime.manifest, "baseline": initial,
                "candidateSpecSha256": {role: digest(spec) for role, spec in candidate.items()},
                "identityActivationAuthorized": False}
    journal.begin(metadata)
    runtime.retention(exact=True, seal=seal)
    journal.advance("gateway-submission-pending")
    runtime.submit("gateway", original["gateway"], candidate["gateway"])
    gateway = runtime.wait("gateway", candidate["gateway"], True)
    require(gateway["version"] > initial["gateway"]["version"])
    wait_chat_readiness(lambda: verify_both(candidate["gateway"], original["chat"]["Spec"], True, False))
    journal.advance("gateway-verified", gateway)
    require(runtime.inspect("chat") == original["chat"])
    runtime.verify("gateway", candidate["gateway"], True)
    journal.advance("chat-submission-pending")
    runtime.submit("chat", original["chat"], candidate["chat"])
    chat = runtime.wait("chat", candidate["chat"], True)
    require(chat["version"] > initial["chat"]["version"])
    runtime.verify("gateway", candidate["gateway"], True)
    wait_chat_readiness(lambda: verify_both(candidate["gateway"], candidate["chat"], True, True))
    journal.advance("chat-verified", chat)
    runtime.retention()
    gateway = runtime.verify("gateway", candidate["gateway"], True)
    chat = runtime.verify("chat", candidate["chat"], True)
    final = journal.advance("complete", {"gateway": gateway, "chat": chat, "identityEnabled": False})
    return {"migration": "complete", "receiptSha256": digest(final), "identityActivationAuthorized": False}


def manifest():
    value = globals().get("BUNDLED_CHAT_BUILD")
    if value is None:
        value = json.loads(Path(__file__).with_name("staging-chat-private-build.json").read_text())
    require(set(value) == {"schemaVersion", "repository", "workflowPath", "sourceCommit", "sourceTree", "image",
                           "runId", "attempt", "artifactId", "artifactDigest", "receiptFileSha256"})
    require(value["schemaVersion"] == 1 and value["repository"] == "olivium-dev/chat-service"
            and value["workflowPath"] == ".github/workflows/jeeb-chat-image-only.yml")
    for key in ("sourceCommit", "sourceTree"):
        identifier(value[key], r"[0-9a-f]{40}")
    for key in ("runId", "attempt", "artifactId"):
        identifier(value[key], r"[1-9][0-9]*")
    identifier(value["image"], r"ghcr\.io/olivium-dev/chat-service@sha256:[0-9a-f]{64}")
    identifier(value["artifactDigest"], r"sha256:[0-9a-f]{64}")
    identifier(value["receiptFileSha256"], r"[0-9a-f]{64}")
    return value


@contextlib.contextmanager
def diagnostic_lock(home, custody):
    """Shared observation of the existing canonical lock; never create anything."""
    home = Path(home)
    require(home.is_absolute() and home.resolve() == home)
    custody.directory(home / ".jeeb-deploy")
    root = custody.directory(home / ".jeeb-deploy/locks")
    with custody.open_directory(root) as directory:
        fd = os.open("jeeb-staging-gateway.lock", os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
        try:
            info = os.fstat(fd)
            require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid() and info.st_nlink == 1
                    and stat.S_IMODE(info.st_mode) == 0o600)
            fcntl.flock(fd, fcntl.LOCK_SH | fcntl.LOCK_NB)
            def unchanged():
                current = os.stat("jeeb-staging-gateway.lock", dir_fd=directory, follow_symlinks=False)
                require((current.st_dev, current.st_ino, current.st_mode, current.st_uid, current.st_nlink)
                        == (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_nlink))
                try:
                    os.stat("jeeb-staging-gateway.owner", dir_fd=directory, follow_symlinks=False)
                except FileNotFoundError:
                    return
                require(False)
            unchanged()
            yield
            unchanged()
        finally:
            os.close(fd)


def diagnostic_failure(stage, error):
    result = {"stage": stage, "passed": False, "code": "guard_failed"}
    if isinstance(error, PermissionError): result["code"] = "permission_denied"
    elif isinstance(error, FileNotFoundError): result["code"] = "missing_evidence"
    elif isinstance(error, BlockingIOError): result["code"] = "lock_busy"
    allowed = {"staging-chat-private-migration.py", "staging-delivery-current-baseline.py",
               "staging-paired-readonly-audit.py", "staging-paired-custody.py"}
    trace = error.__traceback__
    while trace:
        filename = Path(trace.tb_frame.f_code.co_filename).name
        function = trace.tb_frame.f_code.co_name
        if filename in allowed and function != "require" and re.fullmatch(r"[a-z_]+", function):
            result.update(helper=filename, function=function, line=trace.tb_lineno)
        trace = trace.tb_next
    return result


def diagnostic_journal(home, custody, approved_seal):
    """Fixed, private phase-chain projection, never its bodies or arbitrary names."""
    report = {"state": "absent", "phases": [], "gatewayClaimPresent": False, "chatClaimPresent": False}
    path = Path(home) / ".jeeb-deploy/paired-releases" / JOURNAL_NAME
    try:
        custody.directory(path)
    except FileNotFoundError:
        return report, None
    except Exception as error:
        report.update(state="invalid", gatewayClaimPresent=None, chatClaimPresent=None,
                      failure=diagnostic_failure("journal", error))
        return report, None
    report.update(state="invalid", gatewayClaimPresent=None, chatClaimPresent=None)
    try:
        with custody.open_directory(path) as directory:
            with os.scandir(directory) as entries:
                names = set()
                for _ in range(7):
                    entry = next(entries, None)
                    if entry is None: break
                    names.add(entry.name)
        expected = [f"{index:02d}-{phase}.json" for index, phase in enumerate(PHASES)]
        require(len(names) <= 6)
        report["gatewayClaimPresent"] = expected[1] in names
        report["chatClaimPresent"] = expected[3] in names
        require(0 < len(names) <= len(expected) and names == set(expected[:len(names)]))
        previous, fingerprints = None, []
        for index, name in enumerate(expected[:len(names)]):
            value = custody.read_private(path / name, 0o400)
            require(value["phase"] == PHASES[index] and isinstance(value["metadata"], dict))
            metadata = value["metadata"]
            require(type(metadata.get("schemaVersion")) is int and metadata["schemaVersion"] == 1
                    and metadata.get("identityActivationAuthorized") is False
                    and metadata.get("currentBaselineSeal") == approved_seal)
            identifier(metadata.get("sourceCommit"), r"[0-9a-f]{40}")
            for key in ("runId", "attempt"): identifier(metadata.get(key), r"[1-9][0-9]*")
            require(set(value) == ({"phase", "metadata"} if index == 0 else {"phase", "metadata", "previousSha256", "evidence"}))
            if previous is not None:
                require(value["metadata"] == previous["metadata"] and value["previousSha256"] == digest(previous))
                require(isinstance(value["evidence"], dict))
            fingerprints.append(digest(value))
            previous = value
        report.update(state="complete" if len(names) == len(expected) else "prefix", phases=list(PHASES[:len(names)]))
        return report, fingerprints
    except Exception as error:
        report["failure"] = diagnostic_failure("journal", error)
        return report, None


def diagnose(baseline, home, approved_seal):
    """GET-only diagnosis. Evidence does not grant mutation or retry authority."""
    report = {"readOnly": True, "mutationAuthorized": False, "retryAuthorized": False,
              "identityActivationAuthorized": False, "snapshotStable": False, "checks": [], "runtimeFormats": {}}
    stage = "existing-lock"
    try:
        with diagnostic_lock(home, baseline.c):
            stage = "host-and-baseline-snapshot"
            before = baseline.a.collect(home)  # Existing host guard runs before any journal read.
            stage = "journal"
            journal, journal_fingerprint = diagnostic_journal(home, baseline.c, approved_seal)
            report["journal"] = journal
            stage = "baseline-match"
            baseline_witness = None
            try:
                sealed, evidence = baseline.load_seal(home)
                require(baseline.digest(sealed) == approved_seal)
                baseline.verified(before, initial=False)
                require(baseline.origin(home, before["metadata"]) == evidence["originals"])
                saved = evidence["snapshot"]
                require(before["metadata"] == saved["metadata"] and before["secret"] == saved["secret"])
                require(before["info"]["ID"] == saved["info"]["ID"] and
                        before["info"]["Swarm"]["NodeID"] == saved["info"]["Swarm"]["NodeID"])
                require(before["services"] == saved["services"])
                baseline_witness = (sealed, evidence["originals"])
                report["checks"].append({"stage": stage, "passed": True})
            except Exception as error:
                report["checks"].append(diagnostic_failure(stage, error))
            services, observed = {}, {}
            def observed_get(kind, value):
                current = engine_get(kind, value)
                normalized = stable_container(current) if kind == "container" else current
                require(observed.setdefault((kind, value), normalized) == normalized)
                return current
            for role in SERVICES:
                stage = role + "-runtime"
                try:
                    service = engine_get("service", SERVICES[role])
                    services[role] = service
                    spec = service["Spec"]
                    declaration = spec["TaskTemplate"]["ContainerSpec"]
                    formats = {"serviceUserAbsent": "User" not in declaration,
                               "serviceUserExplicitEmpty": declaration.get("User") == ""}
                    if role == "chat":
                        targets = [item.get("File", {}).get("Name") for item in declaration.get("Secrets", [])]
                        formats.update(signingTargetBasename="jeeb-firebase-adminsdk.json" in targets,
                                       signingTargetAbsolute="/run/secrets/jeeb-firebase-adminsdk.json" in targets)
                    report["runtimeFormats"][role] = formats
                    private = (env_map(spec["TaskTemplate"]["ContainerSpec"].get("Env", [])).get("ChatServiceApi__BaseUrl") == PRIVATE_URL
                               if role == "gateway" else not spec.get("EndpointSpec", {}).get("Ports"))
                    result = verified_runtime(role, service, observed_get, manifest(), private)
                    require(result["nodeId"] == before["info"]["Swarm"]["NodeID"])
                    report["checks"].append({"stage": stage, "passed": True, "privateTopology": private})
                except Exception as error:
                    report["checks"].append(diagnostic_failure(stage, error))
            stage = "stable-checkpoint"
            after = baseline.a.collect(home)
            journal_after, fingerprint_after = diagnostic_journal(home, baseline.c, approved_seal)
            require(journal_after == journal and fingerprint_after == journal_fingerprint)
            require(journal["state"] != "invalid")
            require(baseline.stable_runtime(before) == baseline.stable_runtime(after))
            if baseline_witness is not None:
                require(baseline.load_seal(home)[0] == baseline_witness[0]
                        and baseline.origin(home, after["metadata"]) == baseline_witness[1])
            require(len(services) == len(SERVICES) and all(engine_get("service", SERVICES[role]) == service for role, service in services.items()))
            for (kind, value), captured in observed.items():
                current = engine_get(kind, value)
                require((stable_container(current) if kind == "container" else current) == captured)
            report["snapshotStable"] = True
            report["checks"].append({"stage": stage, "passed": True})
    except Exception as error:
        report["snapshotStable"] = False
        report["checks"].append(diagnostic_failure(stage, error))
    return report


def validate_operation(operation):
    # Inventory remains unproven; neither its successful run nor a migrated
    # topology is a substitute for the still-required positive ingress proof.
    require(operation in ("migrate-private", "diagnose-private"))


def bundle():
    directory = Path(__file__).parent
    print("import sys,types")
    for name, filename in (("baseline_audit", "staging-paired-readonly-audit.py"),
                           ("baseline_custody", "staging-paired-custody.py"),
                           ("migration_baseline", "staging-delivery-current-baseline.py")):
        source = (directory / filename).read_text()
        print(f"m=types.ModuleType({name!r});m.BASELINE_BUNDLED=True;sys.modules[{name!r}]=m")
        print(f"exec(compile({source!r},{filename!r},'exec'),m.__dict__)")
    print("BUNDLED_CHAT_BUILD=" + repr(manifest()))
    print(f"exec(compile({Path(__file__).read_text()!r},'staging-chat-private-migration.py','exec'),globals())")


def main():
    try:
        args = sys.argv[1:]
        if args == ["bundle"]:
            bundle()
            return 0
        if len(args) == 2 and args[0] == "validate-operation":
            validate_operation(args[1])
            return 0
        require(len(args) == 5)
        operation, source, run, attempt, seal = args
        validate_operation(operation)
        identifier(source, r"[0-9a-f]{40}")
        identifier(run, r"[1-9][0-9]*")
        identifier(attempt, r"[1-9][0-9]*")
        identifier(seal, r"[0-9a-f]{64}")
        require("migration_baseline" in sys.modules)
        baseline = sys.modules["migration_baseline"]
        if operation == "diagnose-private":
            report = diagnose(baseline, Path.home(), seal)
            report.update(sourceCommit=source, runId=run, attempt=attempt)
            print(json.dumps(report, sort_keys=True))
            return 0
        owner, home = secrets.token_hex(32), Path.home()
        with baseline.c.held_lock(home, owner):
            runtime = Runtime(baseline, manifest(), owner, home, seal)
            result = migrate(runtime, Journal(home, baseline.c), source, run, attempt, seal)
        print(json.dumps(result, sort_keys=True))
        return 0
    except Exception:
        print("Private chat operation stopped. Any submission claim is consumed; reconcile before further action. Identity activation remains unauthorized.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
