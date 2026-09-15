#!/usr/bin/python3 -I
"""One explicitly approved new development Auth fixture; no workflow dispatch.

Secrets and identifiers stay in memory or an immediately unlinked private file.
A durable nonsecret attempt interlock prevents recreation/remint after any exit.
Read the companion runbook before execution. No provider configuration writes.
"""
from __future__ import annotations

import base64
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import secrets
import selectors
import signal
import ssl
import stat
import subprocess
import sys
import tempfile
import time

PROJECT = "jeeb-development-msi"
PACKAGE = "app.jeeb.mobile.dev"
REPO = "olivium-dev/jeeb-gateway"
ENVIRONMENT = "development-msi-gateway-signing"
PROBE_SECRET = "JEEB_DEVELOPMENT_FIREBASE_DIAGNOSTIC_PROBE_JSON"
SIGNING_SECRET = "JEEB_MSI_GATEWAY_ARTIFACT_SIGNING_KEY_PEM"
REQUIRED_PERMISSIONS = frozenset({"resourcemanager.projects.get", "firebase.projects.get",
    "firebase.clients.get", "firebase.clients.list", "firebaseauth.configs.get", "firebaseauth.users.create",
    "serviceusage.services.use"})
MINTER_HASH = "e22ab00c5abe1c3ed017b60d38968e7c61bef76bc63680d303720aed6e53fc93"
MAX_BYTES = 1024 * 1024
MAX_PAGES = 10
EXECUTION_FLAGS = frozenset({"--execute", "--approve-new-nonroutable-fixture",
                           "--accept-retained-fixture", "--acknowledge-row20-window"})


SAFE_REASONS = frozenset(['ambient_tls_override_rejected', 'app_identity_invalid', 'app_project_mismatch', 'apps_pagination_incomplete', 'apps_shape_invalid', 'attempt_directory_unsafe', 'attempt_marker_unsafe', 'command_output_too_large', 'command_timeout', 'config_app_mismatch', 'config_client_ambiguous', 'config_client_invalid', 'config_encoding_invalid', 'config_key_ambiguous', 'config_key_invalid', 'config_project_mismatch', 'config_size_invalid', 'development_app_ambiguous', 'development_project_access_denied', 'development_project_mismatch', 'duplicate_json_key', 'environment_branch_policy_unproved', 'execution_failed', 'execution_interrupted', 'existing_probe_requires_manual_reconciliation', 'explicit_owner_approvals_required', 'fixture_create_result_ambiguous', 'github_metadata_unavailable', 'host_not_allowed', 'isolated_mode_required', 'json_invalid', 'json_size_invalid', 'mint_evidence_invalid', 'mint_failed', 'mint_input_file_unsafe', 'mint_input_size_invalid', 'mint_input_unlink_failed', 'mint_probe_invalid', 'minter_file_unsafe', 'minter_source_mismatch', 'oauth_invalid', 'oauth_unavailable', 'page_token_invalid', 'password_provider_not_enabled', 'prior_attempt_requires_manual_reconciliation', 'probe_lifetime_invalid', 'probe_metadata_unconfirmed', 'probe_shadow_present', 'probe_write_ambiguous', 'project_number_invalid', 'provider_http_error', 'provider_project_mismatch', 'required_permissions_missing', 'signing_secret_unavailable', 'source_head_mismatch', 'source_head_moved', 'source_not_protected', 'source_sha_invalid', 'source_tree_dirty', 'stdin_contract_invalid', 'stdin_size_invalid', 'transport_scope_invalid', 'transport_scope_unproved'])


class Rejected(Exception):
    pass


def require(ok, reason):
    if not ok:
        raise Rejected(reason)


def unique(pairs):
    result = {}
    for k, v in pairs:
        require(k not in result, "duplicate_json_key")
        result[k] = v
    return result


def decode(raw):
    require(0 < len(raw) <= MAX_BYTES, "json_size_invalid")
    try:
        return json.loads(raw, object_pairs_hook=unique)
    except Rejected:
        raise
    except Exception:
        raise Rejected("json_invalid") from None


def bounded_command(args, *, cwd=None, data=None, stdin_file=None):
    """No inherited credential overrides; cap both streams before accumulation."""
    require(data is None or stdin_file is None, "stdin_contract_invalid")
    require(data is None or len(data) <= 32 * 1024, "stdin_size_invalid")
    env = {"HOME": str(Path.home()), "PATH": "/usr/bin:/bin:/opt/homebrew/bin",
           "LANG": "en_US.UTF-8", "GH_PROMPT_DISABLED": "1",
           "CLOUDSDK_CORE_DISABLE_PROMPTS": "1"}
    proc = subprocess.Popen(args, cwd=cwd, env=env, start_new_session=True,
                            stdin=stdin_file if stdin_file is not None else
                            subprocess.PIPE if data is not None else subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    streams = {"out": bytearray(), "err": bytearray()}
    pending = memoryview(data or b"")
    deadline = time.monotonic() + 30
    try:
        with selectors.DefaultSelector() as selector:
            for key, stream in (("out", proc.stdout), ("err", proc.stderr)):
                os.set_blocking(stream.fileno(), False)
                selector.register(stream, selectors.EVENT_READ, key)
            if data is not None:
                os.set_blocking(proc.stdin.fileno(), False)
                if pending:
                    selector.register(proc.stdin, selectors.EVENT_WRITE, "in")
                else:
                    proc.stdin.close()
            while selector.get_map():
                require(time.monotonic() < deadline, "command_timeout")
                for event, _ in selector.select(min(0.2, max(0, deadline - time.monotonic()))):
                    stream, kind = event.fileobj, event.data
                    if kind == "in":
                        try:
                            count = os.write(stream.fileno(), pending[:8192])
                            pending = pending[count:]
                        except BrokenPipeError:
                            pending = pending[:0]
                        if not pending:
                            selector.unregister(stream)
                            stream.close()
                    else:
                        block = os.read(stream.fileno(), 8192)
                        if not block:
                            selector.unregister(stream)
                            stream.close()
                        else:
                            require(len(streams[kind]) + len(block) <= MAX_BYTES,
                                    "command_output_too_large")
                            streams[kind].extend(block)
        code = proc.wait(timeout=max(0.01, deadline - time.monotonic()))
        return code, bytes(streams["out"]), bytes(streams["err"])
    except BaseException:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        proc.wait(timeout=2)
        raise
    finally:
        for buffer in streams.values():
            buffer[:] = b"\0" * len(buffer)
        for stream in (proc.stdin, proc.stdout, proc.stderr):
            if stream is not None and not stream.closed:
                stream.close()


def https_json(host, method, path, token, body=None):
    require(host in {"firebase.googleapis.com", "identitytoolkit.googleapis.com",
                     "cloudresourcemanager.googleapis.com"},
            "host_not_allowed")
    context = ssl.create_default_context()
    if hasattr(context, "keylog_filename"):
        context.keylog_filename = None
    connection = http.client.HTTPSConnection(host, timeout=20, context=context)
    # Direct REST calls do not inherit the gcloud CLI quota-project setting.
    # Bind quota consumption to the same fixed development target as resources.
    headers = {"Authorization": "Bearer " + token, "Connection": "close",
               "x-goog-user-project": PROJECT}
    payload = None
    if body is not None:
        payload = json.dumps(body, separators=(",", ":")).encode()
        headers["Content-Type"] = "application/json"
    try:
        connection.request(method, path, body=payload, headers=headers)
        response = connection.getresponse()
        raw = response.read(MAX_BYTES + 1)
        require(response.status == 200, "provider_http_error")
        return decode(raw)
    finally:
        connection.close()


def private_directory(path):
    created = False
    try:
        path.mkdir(mode=0o700)
        created = True
    except FileExistsError:
        pass
    info = path.lstat()
    require(stat.S_ISDIR(info.st_mode) and not path.is_symlink()
            and info.st_uid == os.geteuid() and stat.S_IMODE(info.st_mode) == 0o700,
            "attempt_directory_unsafe")
    if created:
        parent_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.fsync(parent_fd)
        finally:
            os.close(parent_fd)


def create_interlock(directory, handle, head):
    private_directory(directory)
    marker = directory / "attempt.json"
    try:
        fd = os.open(marker, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    except FileExistsError:
        raise Rejected("prior_attempt_requires_manual_reconciliation") from None
    with os.fdopen(fd, "wb") as stream:
        os.fchmod(stream.fileno(), 0o600)
        info = os.fstat(stream.fileno())
        require(stat.S_ISREG(info.st_mode) and info.st_uid == os.geteuid()
                and info.st_nlink == 1 and stat.S_IMODE(info.st_mode) == 0o600,
                "attempt_marker_unsafe")
        value = {"schema": 1, "operation": "row03-auth-probe", "attemptHandle": handle,
                 "sourceSha": head, "startedAt": int(time.time())}
        stream.write(json.dumps(value, separators=(",", ":")).encode())
        stream.flush()
        os.fsync(stream.fileno())
    directory_fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fsync(directory_fd)
    finally:
        os.close(directory_fd)
    # Deliberately never deleted, including after success or interruption.


class Provisioner:
    def __init__(self, root=None, state=None, command=bounded_command, request=https_json,
                 now=time.time):
        self.root = Path(root) if root is not None else Path(__file__).resolve().parent.parent
        self.state = Path(state) if state is not None else Path.home() / ".jeeb-row03-auth-probe"
        self.command, self.request, self.now = command, request, now
        self.handle = None
        self.interlocked = False
        self.minter_source = None

    def cmd(self, args, **kwargs):
        return self.command(args, **kwargs)

    def gh(self, path, *, absent=False):
        code, out, err = self.cmd(["/opt/homebrew/bin/gh", "api", path])
        if code and absent and b"HTTP 404" in err:
            return None
        require(code == 0, "github_metadata_unavailable")
        return decode(out)

    def source_gate(self):
        branch = self.gh("repos/" + REPO + "/branches/main")
        require(branch.get("protected") is True, "source_not_protected")
        head = branch.get("commit", {}).get("sha", "")
        require(re.fullmatch(r"[0-9a-f]{40}", head), "source_sha_invalid")
        code, out, _ = self.cmd(["/usr/bin/git", "rev-parse", "HEAD"], cwd=self.root)
        require(code == 0 and out.decode().strip() == head, "source_head_mismatch")
        code, out, _ = self.cmd(["/usr/bin/git", "status", "--porcelain", "--untracked-files=normal"], cwd=self.root)
        require(code == 0 and out == b"", "source_tree_dirty")
        path = self.root / "scripts/mint-development-firebase-diagnostic-probe.py"
        info = path.lstat()
        require(stat.S_ISREG(info.st_mode) and not path.is_symlink()
                and info.st_nlink == 1 and info.st_uid == os.geteuid()
                and stat.S_IMODE(info.st_mode) & 0o022 == 0, "minter_file_unsafe")
        actual = path.read_bytes()
        code, blob, _ = self.cmd(["/usr/bin/git", "show", head + ":scripts/mint-development-firebase-diagnostic-probe.py"], cwd=self.root)
        require(code == 0 and actual == blob and hashlib.sha256(blob).hexdigest() == MINTER_HASH,
                "minter_source_mismatch")
        self.minter_source = blob
        return head

    def secret_gate(self):
        environment = self.gh("repos/" + REPO + "/environments/" + ENVIRONMENT)
        policy = environment.get("deployment_branch_policy") or {}
        require(policy.get("protected_branches") is True
                and policy.get("custom_branch_policies") is False,
                "environment_branch_policy_unproved")
        base = "repos/" + REPO + "/environments/" + ENVIRONMENT + "/secrets/"
        require(self.gh(base + SIGNING_SECRET).get("name") == SIGNING_SECRET, "signing_secret_unavailable")
        self.require_probe_absent()
        for name in ("MSI_SSH_PASSWORD", "MSI_SSH_KNOWN_HOSTS"):
            path = "orgs/olivium-dev/actions/secrets/" + name
            secret = self.gh(path)
            require(secret.get("visibility") == "selected", "transport_scope_invalid")
            found = False
            complete = False
            for page in range(1, MAX_PAGES + 1):
                value = self.gh(path + "/repositories?per_page=100&page=" + str(page))
                repositories = value.get("repositories")
                require(isinstance(repositories, list), "transport_scope_invalid")
                found |= any(r.get("full_name") == REPO for r in repositories)
                if len(repositories) < 100:
                    complete = True
                    break
            require(complete and found, "transport_scope_unproved")
        for scope in ("repos/" + REPO, "orgs/olivium-dev"):
            require(self.gh(scope + "/actions/secrets/" + PROBE_SECRET, absent=True) is None,
                    "probe_shadow_present")

    def require_probe_absent(self):
        require(self.gh("repos/" + REPO + "/environments/" + ENVIRONMENT
                        + "/secrets/" + PROBE_SECRET, absent=True) is None,
                "existing_probe_requires_manual_reconciliation")

    def project_gate(self):
        code, out, _ = self.cmd(["/opt/homebrew/bin/gcloud", "projects", "describe", PROJECT,
                                "--format=json", "--quiet"])
        require(code == 0, "development_project_access_denied")
        project = decode(out)
        require(project.get("projectId") == PROJECT and project.get("lifecycleState") == "ACTIVE",
                "development_project_mismatch")
        number = str(project.get("projectNumber", ""))
        require(re.fullmatch(r"[0-9]{1,30}", number), "project_number_invalid")
        code, out, _ = self.cmd(["/opt/homebrew/bin/gcloud", "auth", "print-access-token", "--quiet"])
        require(code == 0 and 0 < len(out) < 16384, "oauth_unavailable")
        token = out.decode().strip()
        require(token and not any(c.isspace() for c in token), "oauth_invalid")
        return number, token

    def permission_gate(self, token):
        result = self.request("cloudresourcemanager.googleapis.com", "POST",
                              "/v1/projects/" + PROJECT + ":testIamPermissions", token,
                              {"permissions": sorted(REQUIRED_PERMISSIONS)})
        permissions = result.get("permissions")
        require(isinstance(permissions, list) and all(isinstance(p, str) for p in permissions)
                and REQUIRED_PERMISSIONS.issubset(set(permissions)), "required_permissions_missing")

    def app_key(self, number, token):
        apps, page_token = [], None
        from urllib.parse import quote
        for _ in range(MAX_PAGES):
            path = "/v1beta1/projects/" + PROJECT + "/androidApps?pageSize=100"
            if page_token:
                path += "&pageToken=" + quote(page_token, safe="")
            value = self.request("firebase.googleapis.com", "GET", path, token)
            batch = value.get("apps", [])
            require(isinstance(batch, list), "apps_shape_invalid")
            apps.extend(a for a in batch if a.get("packageName") == PACKAGE and a.get("state") == "ACTIVE")
            page_token = value.get("nextPageToken")
            if not page_token:
                break
            require(isinstance(page_token, str) and len(page_token) <= 4096, "page_token_invalid")
        require(not page_token, "apps_pagination_incomplete")
        require(len(apps) == 1, "development_app_ambiguous")
        app = apps[0]
        app_id = app.get("appId", "")
        require(re.fullmatch(r"[A-Za-z0-9:._-]{1,256}", app_id), "app_identity_invalid")
        require(app.get("projectId") == PROJECT and app.get("name") in {
            "projects/" + PROJECT + "/androidApps/" + app_id,
            "projects/" + number + "/androidApps/" + app_id}, "app_project_mismatch")
        config = self.request("firebase.googleapis.com", "GET",
                              "/v1beta1/projects/" + PROJECT + "/androidApps/" + quote(app_id, safe="") + "/config", token)
        encoded = config.get("configFileContents")
        require(isinstance(encoded, str) and len(encoded) <= MAX_BYTES, "config_size_invalid")
        try:
            client_config = decode(base64.b64decode(encoded, validate=True))
        except Rejected:
            raise
        except Exception:
            raise Rejected("config_encoding_invalid") from None
        project = client_config.get("project_info", {})
        require(project.get("project_id") == PROJECT and str(project.get("project_number")) == number,
                "config_project_mismatch")
        clients = client_config.get("client", [])
        require(isinstance(clients, list), "config_client_invalid")
        matching = [c for c in clients if c.get("client_info", {}).get("android_client_info", {}).get("package_name") == PACKAGE]
        require(len(matching) == 1, "config_client_ambiguous")
        client = matching[0]
        require(client.get("client_info", {}).get("mobilesdk_app_id") == app_id, "config_app_mismatch")
        keys = client.get("api_key", [])
        require(isinstance(keys, list) and len(keys) == 1, "config_key_ambiguous")
        key = keys[0].get("current_key", "")
        require(isinstance(key, str) and re.fullmatch(r"[A-Za-z0-9_-]{20,128}", key), "config_key_invalid")
        return key

    def provider_gate(self, token, number):
        config = self.request("identitytoolkit.googleapis.com", "GET",
                              "/admin/v2/projects/" + PROJECT + "/config", token)
        email = config.get("signIn", {}).get("email", {})
        require(config.get("name") in {"projects/" + PROJECT + "/config",
                                       "projects/" + number + "/config"},
                "provider_project_mismatch")
        require(email.get("enabled") is True and email.get("passwordRequired") is True,
                "password_provider_not_enabled")

    def mint(self, value):
        data = json.dumps(value, separators=(",", ":")).encode()
        require(len(data) <= 4096, "mint_input_size_invalid")
        require(self.minter_source is not None
                and hashlib.sha256(self.minter_source).hexdigest() == MINTER_HASH,
                "minter_source_mismatch")
        fd, name = tempfile.mkstemp(prefix="row03-auth-", suffix=".tmp")
        linked = True
        try:
            os.fchmod(fd, 0o600)
            info = os.fstat(fd)
            require(stat.S_ISREG(info.st_mode) and info.st_uid == os.geteuid()
                    and info.st_nlink == 1 and stat.S_IMODE(info.st_mode) == 0o600,
                    "mint_input_file_unsafe")
            # No secrets have been written yet. Never revisit the name after unlink.
            os.unlink(name)
            linked = False
            require(os.fstat(fd).st_nlink == 0, "mint_input_unlink_failed")
            with os.fdopen(fd, "w+b") as stream:
                fd = -1
                stream.write(data)
                stream.flush()
                stream.seek(0)
                code, raw, err = self.cmd(["/usr/bin/python3", "-I", "-B",
                    "-c", self.minter_source.decode("utf-8")], stdin_file=stream)
            require(code == 0, "mint_failed")
            probe, evidence = decode(raw), decode(err)
            require(set(probe) == {"idToken", "expectedSubject", "expectedProvider"}
                    and probe.get("expectedSubject") == value["expectedUid"]
                    and probe.get("expectedProvider") == "password"
                    and isinstance(probe.get("idToken"), str)
                    and 0 < len(probe["idToken"]) <= 16 * 1024, "mint_probe_invalid")
            expected_fields = {"status", "project", "provider", "uidSha256Prefix", "tokenSha256Prefix", "expiresAt"}
            require(set(evidence) == expected_fields and evidence.get("status") == "probe_minted"
                    and evidence.get("project") == PROJECT and evidence.get("provider") == "password"
                    and evidence.get("uidSha256Prefix") == hashlib.sha256(value["expectedUid"].encode()).hexdigest()[:16]
                    and evidence.get("tokenSha256Prefix") == hashlib.sha256(probe["idToken"].encode()).hexdigest()[:16],
                    "mint_evidence_invalid")
            self.fresh(evidence)
            return raw, evidence
        finally:
            if fd >= 0:
                os.close(fd)
            if linked:
                try:
                    os.unlink(name)
                except FileNotFoundError:
                    pass

    def fresh(self, evidence):
        expiry = evidence.get("expiresAt")
        require(type(expiry) is int and 1800 <= expiry - int(self.now()) <= 3660,
                "probe_lifetime_invalid")

    def execute(self):
        # Any prior attempt stops even read-only setup on restart; no automatic reset.
        require(not os.path.lexists(self.state / "attempt.json"),
                "prior_attempt_requires_manual_reconciliation")
        head = self.source_gate()
        self.secret_gate()
        number, token = self.project_gate()
        self.permission_gate(token)
        key = self.app_key(number, token)
        self.provider_gate(token, number)
        self.require_probe_absent()
        require(self.source_gate() == head, "source_head_moved")
        self.handle = secrets.token_hex(16)
        self.interlocked = True
        create_interlock(self.state, self.handle, head)
        uid = "row03-" + secrets.token_hex(24)
        email = secrets.token_hex(24) + "@row03.invalid"
        password = secrets.token_urlsafe(48)
        created = self.request("identitytoolkit.googleapis.com", "POST", "/v1/accounts:signUp", token,
                              {"targetProjectId": PROJECT, "localId": uid, "email": email,
                               "password": password, "displayName": "ROW03-" + self.handle,
                               "emailVerified": False, "disabled": False})
        require(created.get("localId") == uid, "fixture_create_result_ambiguous")
        raw, evidence = self.mint({"webApiKey": key, "email": email, "password": password, "expectedUid": uid})
        self.require_probe_absent()
        self.fresh(evidence)
        code, _, _ = self.cmd(["/opt/homebrew/bin/gh", "secret", "set", PROBE_SECRET,
                              "--repo", REPO, "--env", ENVIRONMENT], data=raw)
        require(code == 0, "probe_write_ambiguous")
        metadata = self.gh("repos/" + REPO + "/environments/" + ENVIRONMENT + "/secrets/" + PROBE_SECRET)
        require(metadata.get("name") == PROBE_SECRET, "probe_metadata_unconfirmed")
        self.fresh(evidence)
        return {"status": "probe_secret_metadata_confirmed", "project": PROJECT,
                "attemptHandle": self.handle, "fixtureRetained": True,
                "expiresAt": evidence["expiresAt"], "sourceSha": head,
                "workflowDispatched": False}


def run(argv, *, provisioner=None, stdout=sys.stdout, isolated=None):
    require_isolated = sys.flags.isolated if isolated is None else isolated
    worker = provisioner
    try:
        require(require_isolated, "isolated_mode_required")
        require(not any(os.environ.get(k) for k in ("SSL_CERT_FILE", "SSL_CERT_DIR", "SSLKEYLOGFILE")),
                "ambient_tls_override_rejected")
        require(len(argv) == len(EXECUTION_FLAGS) + 1 and set(argv[1:]) == EXECUTION_FLAGS,
                "explicit_owner_approvals_required")
        worker = worker or Provisioner()
        result = worker.execute()
        print(json.dumps(result, separators=(",", ":")), file=stdout)
        return 0
    except BaseException as exc:
        reason = str(exc) if isinstance(exc, Rejected) else "execution_failed"
        # Never forward arbitrary exception strings or tool/provider output.
        if reason not in SAFE_REASONS:
            reason = "execution_failed"
        value = {"status": "provisioning_stopped", "reason": reason,
                 "manualReconciliationRequired": bool(worker and (worker.interlocked or
                     os.path.lexists(worker.state / "attempt.json")))}
        if worker and worker.handle is not None:
            value["attemptHandle"] = worker.handle
        print(json.dumps(value, separators=(",", ":")), file=stdout)
        return 1


def main():
    def interrupted(_number, _frame):
        raise Rejected("execution_interrupted")
    for name in ("SIGINT", "SIGTERM", "SIGHUP", "SIGPIPE", "SIGQUIT"):
        if hasattr(signal, name):
            signal.signal(getattr(signal, name), interrupted)
    return run(sys.argv)


if __name__ == "__main__":
    raise SystemExit(main())
