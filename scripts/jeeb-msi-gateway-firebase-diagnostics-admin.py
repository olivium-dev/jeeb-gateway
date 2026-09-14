#!/usr/bin/python3 -I
"""Stage and activate one immutable gateway diagnostic runtime on Jeeb MSI.

The root-installed copy is invoked only as ``preflight``, ``stage``, or
``activate``. ``stage`` reads a bounded non-secret runtime archive from stdin.
``activate`` changes only jeeb-gateway.service and automatically restores the
exact predecessor unit state if readiness or the diagnostic route fails.
"""
from __future__ import annotations

import argparse
import base64
from contextlib import contextmanager
import fcntl
import hashlib
import http.client
import json
import os
from pathlib import Path, PurePosixPath
import pwd
import re
import secrets
import shutil
import signal
import socket
import stat
import subprocess
import sys
import tarfile
import tempfile
import time


HOST = "ouday-GT70-2OC-2OD"
UNIT = "jeeb-gateway.service"
USER = "ec2-user"
UID = 1001
GID = 1001
PORT = 10090
REPOSITORY = "olivium-dev/jeeb-gateway"
RUNTIME = "linux-x64"
PROJECT = "jeeb-development-msi"
PREDECESSOR = Path(
    "/home/ec2-user/jeeb-native-builds/20260910/"
    "jeeb-gateway-686b6e08-linux-x64"
)
ENV_FILE = Path("/home/ec2-user/iter5-native/env/gateway.env")
FRAGMENT = Path("/etc/systemd/system/jeeb-gateway.service")
DROPIN_DIRECTORY = Path("/etc/systemd/system/jeeb-gateway.service.d")
DROPIN = DROPIN_DIRECTORY / "zzzzz-firebase-token-diagnostics.conf"
INSTALL_PATH = Path(
    "/usr/local/sbin/jeeb-msi-gateway-firebase-diagnostics-admin"
)
LOCK_PATH = Path("/run/jeeb-msi-service-deploy.lock")
STATE_DIRECTORY = Path("/var/lib/jeeb-msi-gateway-firebase-diagnostics")
STAGED_ARCHIVE = STATE_DIRECTORY / "candidate.tar.gz"
STAGED_RECEIPT = STATE_DIRECTORY / "candidate.json"
CHALLENGE = STATE_DIRECTORY / "challenge.json"
RELEASE_ROOT = Path("/opt/jeeb-gateway-releases/firebase-diagnostics")
ROLLBACK_DIRECTORY = RELEASE_ROOT / "predecessor-686b6e08-linux-x64"
ROLLBACK_RUNTIME = ROLLBACK_DIRECTORY / "runtime"
ROLLBACK_ENV_FILE = ROLLBACK_DIRECTORY / "gateway.env"
MAX_ARCHIVE_BYTES = 256 * 1024 * 1024
MAX_EXPANDED_BYTES = 768 * 1024 * 1024
MAX_MEMBERS = 4096
ARTIFACT_MAGIC = (
    b"JEEB-MSI-GATEWAY-ARTIFACT-V1-RSA3072-SHA256-"
    b"0d88071bfe63915f1c428cc8899b6b56bb78eb8b9bb01fcf61a7910339193247\n"
)
SIGNING_PUBLIC_KEY = b"""-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAqimvAZkdDDDPi44L/Xig
Xbq94vaywGvgEW98lU4rIP68pR2ENTnQPlJP3/ZHYV8PG4cl+K4euc7pkIbkkneE
AM9MxG/nLnhODdl2twIxvLcnsMtXi5aBdeAACMHap7dVaAGWnUCRa3n1vldklNhp
wQcUkpthsIy0eUnzyqu7VaG7596Iuheu7Ta7F92PBEVAZxXQNKZh7HvKYyqUGNFr
tkSB7bcMEaHDxZ+VJax4u4FyD2VL/FrslT8KQvQ5nZQFwBqf/GI8PPT9F5brWWox
RAhR9UetHOwpIHdAJTSZuIzd9CfSATyP7vbnPnhR6SXz5AJRYs/RrR9qC1tIMFGb
ArblK7bt86fjVx1KF6GT3MIIZ6GJx3m225ejR/wot9J1v8LeMhqJ3E+OcSzgmIYT
1ptzHDLibB8FdJNfQffSUJqhjLgKq6oeoRYC6Ur5fXQTBC+eqr/Bfz40uW4LicRf
s6N5ZNKUfUr/nVVr2DVLUGya0qnRb0YKjRh5WbSvDUPpAgMBAAE=
-----END PUBLIC KEY-----
"""
SIGNING_PUBLIC_KEY_SHA256 = (
    "0d88071bfe63915f1c428cc8899b6b56bb78eb8b9bb01fcf61a7910339193247"
)
EXPECTED_FAILING = frozenset(
    {
        "data-export-signing-key",
        "internal-job-token",
        "private-artifact-store-bearer",
    }
)
EXPECTED_DROPINS = frozenset(
    {
        str(DROPIN_DIRECTORY / "90-jeeb-staging-20260828.conf"),
        str(DROPIN_DIRECTORY / "configimport.conf"),
        str(DROPIN_DIRECTORY / "otpescalations-flip.conf"),
        str(DROPIN_DIRECTORY / "prohibiteditems-flip.conf"),
        str(DROPIN_DIRECTORY / "requestsownerlist-flip.conf"),
        str(DROPIN_DIRECTORY / "w504-import-token.conf"),
        str(DROPIN_DIRECTORY / "zzz-delivery-service-auth.conf"),
        str(DROPIN_DIRECTORY / "zzzz-gateway-686b6e08.conf"),
    }
)
EXPECTED_SHA256 = {
    FRAGMENT: "ff075e5f47281e6a5d157ba0547bf0468f5fe8f25b187b1d4dbc4877b8be0934",
    ENV_FILE: "ffc7715021f04f148c4494bbbada943e7c5b447ab7da0d0719d951777681d26b",
    PREDECESSOR / "JeebGateway": "7bee243d6a52c854836fbb4a146fae9577c94dcbdbfbc13db2f1ab79691b95d9",
    PREDECESSOR / "JeebGateway.dll": "a7eb7719a46e92312838ffd3c056bb6bf5578b98664c2c0974dcc9a22b00978b",
}
# Canonical full-tree and drop-in identities are populated from the read-only
# MSI inventory immediately before this helper is committed. Each identity
# covers every path, type, uid/gid/mode/nlink, file size, and file SHA-256.
EXPECTED_PREDECESSOR_TREE_IDENTITY = (
    "736aa77e4ef62e1575f7fb102fd4929f0d214b8fd748bcf29964aa98b806ea48"
)
EXPECTED_PREDECESSOR_TREE_DIRECTORIES = 6
EXPECTED_PREDECESSOR_TREE_FILES = 409
EXPECTED_PREDECESSOR_CONTENT_IDENTITY = (
    "70afa4658dd7ffd054dc56da5c684adf7abd6f0fd41334a42f960143ffec0a57"
)
EXPECTED_DROPIN_IDENTITY = (
    "3b2c8f4a9771fa2be5ec4c5e419adf5cd0e06957bcec82d63a3438933b55bcb6"
)
CHALLENGE_LIFETIME_SECONDS = 10 * 60


def predecessor_binding():
    return (
        "predecessor-v1:"
        + EXPECTED_PREDECESSOR_TREE_IDENTITY
        + ":"
        + EXPECTED_DROPIN_IDENTITY
        + ":"
        + EXPECTED_SHA256[FRAGMENT]
        + ":"
        + EXPECTED_SHA256[ENV_FILE]
    )


class Rejected(Exception):
    pass


def require(value, reason):
    if not value:
        raise Rejected(reason)


def sha256_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while True:
            block = source.read(1024 * 1024)
            if not block:
                return digest.hexdigest()
            digest.update(block)


def extended_attribute_names(path):
    require(hasattr(os, "listxattr"), "extended_attribute_runtime_unavailable")
    return os.listxattr(path, follow_symlinks=False)


def canonical_file_record(label, path):
    info = path.lstat()
    require("\n" not in label and "\0" not in label, "manifest_path_invalid")
    common = [
        label,
        info.st_uid,
        info.st_gid,
        format(stat.S_IMODE(info.st_mode), "04o"),
        info.st_nlink,
    ]
    require(
        not extended_attribute_names(path),
        "manifest_extended_attributes_present",
    )
    if stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode):
        return common + ["directory"]
    require(
        stat.S_ISREG(info.st_mode)
        and not stat.S_ISLNK(info.st_mode)
        and info.st_nlink == 1,
        "manifest_entry_type_invalid",
    )
    return common + ["file", info.st_size, sha256_file(path)]


def canonical_records_identity(records):
    raw = json.dumps(records, ensure_ascii=True, separators=(",", ":")).encode()
    return hashlib.sha256(raw).hexdigest()


def tree_identity(root):
    records = []
    directories = 0
    files = 0
    pending = [(root, ".")]
    while pending:
        path, relative = pending.pop()
        record = canonical_file_record(relative, path)
        records.append(record)
        if record[5] == "directory":
            directories += 1
            children = sorted(path.iterdir(), key=lambda item: item.name, reverse=True)
            for child in children:
                child_relative = child.name if relative == "." else relative + "/" + child.name
                pending.append((child, child_relative))
        else:
            files += 1
    records.sort(key=lambda record: record[0])
    return canonical_records_identity(records), directories, files


def tree_content_identity(root):
    records = []
    directories = 0
    files = 0
    pending = [(root, ".")]
    while pending:
        path, relative = pending.pop()
        info = path.lstat()
        require("\n" not in relative and "\0" not in relative, "manifest_path_invalid")
        require(not extended_attribute_names(path), "manifest_extended_attributes_present")
        if stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode):
            records.append([relative, "directory"])
            directories += 1
            children = sorted(path.iterdir(), key=lambda item: item.name, reverse=True)
            for child in children:
                child_relative = child.name if relative == "." else relative + "/" + child.name
                pending.append((child, child_relative))
        else:
            require(
                stat.S_ISREG(info.st_mode)
                and not stat.S_ISLNK(info.st_mode)
                and info.st_nlink == 1,
                "manifest_entry_type_invalid",
            )
            records.append([relative, "file", info.st_size, sha256_file(path)])
            files += 1
    records.sort(key=lambda record: record[0])
    return canonical_records_identity(records), directories, files


def file_set_identity(paths):
    records = [
        canonical_file_record(raw, Path(raw))
        for raw in sorted(str(path) for path in paths)
    ]
    require(all(record[5] == "file" for record in records), "manifest_entry_type_invalid")
    return canonical_records_identity(records)


def command(args, timeout=30):
    result = subprocess.run(
        args,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=timeout,
        check=False,
        env={"PATH": "/usr/bin:/bin", "LANG": "C"},
    )
    require(
        result.returncode == 0 and len(result.stdout) <= 1024 * 1024,
        "host_command_failed",
    )
    return result.stdout.decode("utf-8", "strict")


def systemctl(*args):
    return command(["/usr/bin/systemctl", *args])


def regular_file(path, uid, gid, mode, reason):
    info = path.lstat()
    require(
        stat.S_ISREG(info.st_mode)
        and not stat.S_ISLNK(info.st_mode)
        and info.st_nlink == 1
        and (info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode))
        == (uid, gid, mode),
        reason,
    )


def protected_directory(path, uid, gid, mode, reason):
    info = path.lstat()
    require(
        stat.S_ISDIR(info.st_mode)
        and not stat.S_ISLNK(info.st_mode)
        and (info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode))
        == (uid, gid, mode),
        reason,
    )


def installed_helper_is_protected():
    regular_file(INSTALL_PATH, 0, 0, 0o755, "installed_helper_metadata_invalid")
    parent = INSTALL_PATH.parent.lstat()
    require(
        stat.S_ISDIR(parent.st_mode)
        and not stat.S_ISLNK(parent.st_mode)
        and parent.st_uid == 0
        and parent.st_gid == 0
        and not (parent.st_mode & (stat.S_IWGRP | stat.S_IWOTH)),
        "installed_helper_parent_invalid",
    )


def ignore_session_termination():
    # Activation is a short transaction with its own verified rollback. Once a
    # fixed helper command starts, an SSH/tunnel loss or workflow cancellation
    # must not strand the unit between mutation and rollback.
    for name in ("SIGHUP", "SIGINT", "SIGTERM", "SIGQUIT"):
        number = getattr(signal, name, None)
        if number is not None:
            signal.signal(number, signal.SIG_IGN)


@contextmanager
def operation_lock():
    run = LOCK_PATH.parent.lstat()
    require(
        stat.S_ISDIR(run.st_mode)
        and not stat.S_ISLNK(run.st_mode)
        and run.st_uid == 0
        and run.st_gid == 0
        and not (run.st_mode & (stat.S_IWGRP | stat.S_IWOTH)),
        "deployment_lock_parent_invalid",
    )
    descriptor = os.open(
        LOCK_PATH,
        os.O_RDWR
        | os.O_CREAT
        | os.O_NONBLOCK
        | getattr(os, "O_NOFOLLOW", 0)
        | getattr(os, "O_CLOEXEC", 0),
        0o600,
    )
    try:
        info = os.fstat(descriptor)
        require(
            stat.S_ISREG(info.st_mode)
            and info.st_nlink == 1
            and info.st_uid == 0
            and info.st_gid == 0
            and stat.S_IMODE(info.st_mode) == 0o600,
            "deployment_lock_invalid",
        )
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise Rejected("deployment_lock_busy") from None
        yield
    finally:
        os.close(descriptor)


def unit_properties(running=True):
    fields = (
        "Id,LoadState,ActiveState,SubState,MainPID,User,Group,FragmentPath,"
        "DropInPaths,WorkingDirectory"
    )
    raw = systemctl("show", UNIT, "--no-pager", "--property=" + fields)
    values = dict(line.split("=", 1) for line in raw.splitlines() if "=" in line)
    require(values.get("Id") == UNIT, "unit_identity_invalid")
    require(values.get("LoadState") == "loaded", "unit_load_state_invalid")
    if running:
        require(
            values.get("ActiveState") == "active"
            and values.get("SubState") == "running",
            "unit_not_running",
        )
    else:
        require(
            values.get("ActiveState") == "inactive"
            and values.get("SubState") == "dead",
            "unit_not_stopped",
        )
    require(
        values.get("User") == USER and values.get("Group") == USER,
        "unit_owner_invalid",
    )
    require(values.get("FragmentPath") == str(FRAGMENT), "unit_fragment_invalid")
    if running:
        require(
            values.get("MainPID", "").isdigit() and int(values["MainPID"]) > 1,
            "unit_pid_invalid",
        )
    else:
        require(values.get("MainPID") == "0", "unit_stopped_pid_invalid")
    return values


def process_environment_values(pid, key):
    raw = (Path("/proc") / str(pid) / "environ").read_bytes()
    require(len(raw) <= 1024 * 1024, "process_environment_too_large")
    prefix = (key + "=").encode()
    values = [
        value[len(prefix) :].decode("utf-8", "strict")
        for value in raw.split(b"\0")
        if value.startswith(prefix)
    ]
    return values


def process_environment(pid, key):
    values = process_environment_values(pid, key)
    require(len(values) == 1, "process_environment_binding_ambiguous")
    return values[0]


def dropin_set(values):
    raw = values.get("DropInPaths", "")
    require("\n" not in raw, "dropin_metadata_invalid")
    return frozenset(raw.split())


def validate_predecessor_files():
    regular_file(FRAGMENT, 0, 0, 0o644, "unit_fragment_metadata_invalid")
    regular_file(ENV_FILE, UID, GID, 0o600, "gateway_env_metadata_invalid")
    regular_file(
        PREDECESSOR / "JeebGateway",
        UID,
        GID,
        0o700,
        "predecessor_entrypoint_metadata_invalid",
    )
    regular_file(
        PREDECESSOR / "JeebGateway.dll",
        UID,
        GID,
        0o600,
        "predecessor_dll_metadata_invalid",
    )
    for path, expected in EXPECTED_SHA256.items():
        require(
            not extended_attribute_names(path),
            "predecessor_extended_attributes_present",
        )
        require(sha256_file(path) == expected, "predecessor_digest_invalid")
    tree_digest, directory_count, file_count = tree_identity(PREDECESSOR)
    require(
        tree_digest == EXPECTED_PREDECESSOR_TREE_IDENTITY
        and directory_count == EXPECTED_PREDECESSOR_TREE_DIRECTORIES
        and file_count == EXPECTED_PREDECESSOR_TREE_FILES,
        "predecessor_tree_identity_invalid",
    )
    require(
        file_set_identity(EXPECTED_DROPINS) == EXPECTED_DROPIN_IDENTITY,
        "predecessor_dropin_identity_invalid",
    )


def health_request(path, method="GET", body=None):
    connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=5)
    try:
        headers = {"Host": "127.0.0.1", "Connection": "close"}
        if body is not None:
            headers["Content-Type"] = "application/json"
        connection.request(method, path, body=body, headers=headers)
        response = connection.getresponse()
        raw = response.read(65537)
        require(len(raw) <= 65536, "http_response_too_large")
        return response.status, raw
    finally:
        connection.close()


def readiness_snapshot(baseline=None):
    live_status, _ = health_request("/health/live")
    require(live_status == 200, "liveness_failed")
    status, raw = health_request("/health/ready")
    require(status == 200, "readiness_http_failed")
    try:
        body = json.loads(raw)
    except Exception:
        raise Rejected("readiness_json_invalid") from None
    require(isinstance(body, dict) and isinstance(body.get("checks"), list), "readiness_shape_invalid")
    statuses = {}
    for check in body["checks"]:
        require(
            isinstance(check, dict)
            and isinstance(check.get("name"), str)
            and check.get("status") in {"Healthy", "Degraded", "Unhealthy"}
            and check["name"] not in statuses,
            "readiness_check_invalid",
        )
        statuses[check["name"]] = check["status"]
    failing = {name for name, check_status in statuses.items() if check_status != "Healthy"}
    require(
        isinstance(body.get("failing"), list)
        and set(body["failing"]) == failing
        and len(body["failing"]) == len(failing),
        "readiness_failing_invalid",
    )
    if baseline is None:
        require(
            body.get("status") == "Degraded" and failing == EXPECTED_FAILING,
            "readiness_baseline_changed",
        )
    else:
        require(set(statuses) == set(baseline), "readiness_roster_changed")
        rank = {"Healthy": 0, "Degraded": 1, "Unhealthy": 2}
        require(
            all(rank[statuses[name]] <= rank[baseline[name]] for name in baseline),
            "readiness_regressed",
        )
        require(body.get("status") in {"Healthy", "Degraded"}, "readiness_status_invalid")
    return statuses


def wait_readiness(baseline, attempts=30):
    for _ in range(attempts):
        try:
            readiness_snapshot(baseline)
            return
        except Exception:
            time.sleep(1)
    raise Rejected("readiness_failed")


def diagnostic_probe():
    raw = json.dumps(
        {
            "idToken": "invalid.jwt.diagnostic-probe",
            "expectedProjectId": PROJECT,
            "expectedSubject": "gateway-msi-diagnostic-probe",
        },
        separators=(",", ":"),
    ).encode()
    for route in (
        "/v1/auth/diagnostics/firebase-token",
        "/auth/diagnostics/firebase-token",
    ):
        status, response = health_request(route, "POST", raw)
        try:
            body = json.loads(response)
        except Exception:
            raise Rejected("diagnostic_probe_json_invalid") from None
        require(
            status == 401 and body == {"verified": False},
            "diagnostic_probe_failed",
        )


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "probe_duplicate_key")
        result[key] = value
    return result


def validate_probe(raw):
    require(0 < len(raw) <= 20 * 1024, "probe_size_invalid")
    try:
        value = json.loads(raw, object_pairs_hook=unique_object)
    except Rejected:
        raise
    except Exception:
        raise Rejected("probe_json_invalid") from None
    require(
        isinstance(value, dict)
        and set(value) == {"idToken", "expectedSubject", "expectedProvider"}
        and isinstance(value["idToken"], str)
        and 0 < len(value["idToken"]) <= 16 * 1024
        and isinstance(value["expectedSubject"], str)
        and 0 < len(value["expectedSubject"]) <= 128
        and isinstance(value["expectedProvider"], str)
        and re.fullmatch(r"[A-Za-z0-9._-]{1,64}", value["expectedProvider"]),
        "probe_contract_invalid",
    )
    return value


def successful_diagnostic_probe(probe):
    request = json.dumps(
        {
            "idToken": probe["idToken"],
            "expectedProjectId": PROJECT,
            "expectedSubject": probe["expectedSubject"],
        },
        separators=(",", ":"),
    ).encode()
    expected_hash = hashlib.sha256(probe["expectedSubject"].encode()).hexdigest()
    expected = {
        "verified": True,
        "projectId": PROJECT,
        "provider": probe["expectedProvider"],
        "subjectSha256": expected_hash,
    }
    for route in (
        "/v1/auth/diagnostics/firebase-token",
        "/auth/diagnostics/firebase-token",
    ):
        status, response = health_request(route, "POST", request)
        try:
            body = json.loads(response)
        except Exception:
            raise Rejected("diagnostic_success_json_invalid") from None
        require(status == 200 and body == expected, "diagnostic_success_failed")


def host_preflight(candidate=None, readiness_baseline=None):
    require(
        sys.platform == "linux"
        and os.geteuid() == 0
        and socket.gethostname() == HOST,
        "host_identity_invalid",
    )
    installed_helper_is_protected()
    protected_directory(
        STATE_DIRECTORY.parent,
        0,
        0,
        0o755,
        "state_parent_directory_invalid",
    )
    protected_directory(
        RELEASE_ROOT.parent.parent,
        0,
        0,
        0o755,
        "release_parent_directory_invalid",
    )
    account = pwd.getpwnam(USER)
    require((account.pw_uid, account.pw_gid) == (UID, GID), "service_account_invalid")
    protected_directory(
        DROPIN_DIRECTORY,
        0,
        0,
        0o755,
        "unit_dropin_directory_invalid",
    )
    values = unit_properties()
    expected_dropins = EXPECTED_DROPINS | ({str(DROPIN)} if candidate else set())
    require(dropin_set(values) == expected_dropins, "unit_dropins_changed")
    expected_directory = candidate or PREDECESSOR
    require(
        values.get("WorkingDirectory") == str(expected_directory),
        "unit_working_directory_invalid",
    )
    pid = int(values["MainPID"])
    require(
        (Path("/proc") / str(pid) / "cwd").resolve() == expected_directory,
        "process_working_directory_invalid",
    )
    require(
        process_environment(pid, "ASPNETCORE_ENVIRONMENT") == "Production",
        "gateway_host_environment_invalid",
    )
    if candidate:
        regular_dropin(candidate)
        require(
            process_environment(pid, "Auth__FirebaseTokenDiagnostics__Enabled") == "true"
            and process_environment(pid, "Auth__FirebaseTokenDiagnostics__Environment")
            == "development"
            and process_environment(pid, "Auth__FirebaseTokenDiagnostics__ProjectId") == PROJECT,
            "diagnostic_process_binding_invalid",
        )
    else:
        require(
            all(
                not process_environment_values(pid, key)
                for key in (
                    "Auth__FirebaseTokenDiagnostics__Enabled",
                    "Auth__FirebaseTokenDiagnostics__Environment",
                    "Auth__FirebaseTokenDiagnostics__ProjectId",
                )
            ),
            "predecessor_diagnostic_binding_present",
        )
        validate_predecessor_files()
    readiness_snapshot(readiness_baseline)
    return values


def stopped_predecessor_preflight():
    values = unit_properties(running=False)
    require(dropin_set(values) == EXPECTED_DROPINS, "unit_dropins_changed")
    require(
        values.get("WorkingDirectory") == str(PREDECESSOR),
        "unit_working_directory_invalid",
    )
    validate_predecessor_files()


def manifest_from_archive(archive):
    try:
        with tarfile.open(archive, "r:gz") as bundle:
            members = bundle.getmembers()
            require(0 < len(members) <= MAX_MEMBERS, "artifact_member_count_invalid")
            expanded = 0
            names = set()
            for member in members:
                path = PurePosixPath(member.name)
                require(
                    not path.is_absolute()
                    and ".." not in path.parts
                    and path.parts
                    and str(path) == member.name
                    and (
                        path.parts[0] == "payload"
                        or (len(path.parts) == 1 and path.parts[0] == "manifest.json")
                    ),
                    "artifact_path_invalid",
                )
                require(member.name not in names, "artifact_duplicate_path")
                names.add(member.name)
                require(member.isdir() or member.isfile(), "artifact_type_invalid")
                require(member.uid == 0 and member.gid == 0, "artifact_owner_invalid")
                if member.isfile():
                    expanded += member.size
            require(expanded <= MAX_EXPANDED_BYTES, "artifact_expanded_size_invalid")
            manifest_member = bundle.getmember("manifest.json")
            require(manifest_member.isfile(), "artifact_manifest_invalid")
            manifest_raw = bundle.extractfile(manifest_member).read(65537)
            require(len(manifest_raw) <= 65536, "artifact_manifest_too_large")
            manifest = json.loads(manifest_raw)
    except Rejected:
        raise
    except Exception:
        raise Rejected("artifact_invalid") from None
    require(
        isinstance(manifest, dict)
        and set(manifest)
        == {"schema", "repository", "commitSha", "sourceTree", "runtime"}
        and manifest["schema"] == 1
        and manifest["repository"] == REPOSITORY
        and manifest["runtime"] == RUNTIME
        and re.fullmatch(r"[0-9a-f]{40}", manifest["commitSha"] or "")
        and re.fullmatch(r"[0-9a-f]{40}", manifest["sourceTree"] or ""),
        "artifact_manifest_invalid",
    )
    require("payload/JeebGateway" in names, "artifact_entrypoint_missing")
    require("payload/JeebGateway.dll" in names, "artifact_dll_missing")
    require("payload/JeebGateway.runtimeconfig.json" in names, "artifact_runtime_missing")
    return manifest


def ensure_root_directory(path, mode):
    if not path.exists():
        path.mkdir(mode=mode, parents=False)
    protected_directory(path, 0, 0, mode, "state_directory_invalid")


def write_staged_archive(source):
    ensure_root_directory(STATE_DIRECTORY, 0o700)
    descriptor, temporary = tempfile.mkstemp(prefix=".candidate.", dir=STATE_DIRECTORY)
    digest = hashlib.sha256()
    total = 0
    try:
        with os.fdopen(descriptor, "wb", closefd=True) as target:
            while True:
                block = source.read(1024 * 1024)
                if not block:
                    break
                total += len(block)
                require(total <= MAX_ARCHIVE_BYTES, "artifact_too_large")
                digest.update(block)
                target.write(block)
            require(total > 0, "artifact_empty")
            target.flush()
            os.fsync(target.fileno())
            os.fchmod(target.fileno(), 0o600)
        require(not STAGED_ARCHIVE.exists(), "staged_artifact_collision")
        os.replace(temporary, STAGED_ARCHIVE)
    finally:
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass
    regular_file(STAGED_ARCHIVE, 0, 0, 0o600, "staged_artifact_metadata_invalid")
    return digest.hexdigest()


def read_signed_artifact(source):
    require(source.readline(128) == ARTIFACT_MAGIC, "artifact_envelope_invalid")
    nonce_line = source.readline(128)
    binding_line = source.readline(512)
    require(
        nonce_line.endswith(b"\n")
        and re.fullmatch(rb"[0-9a-f]{64}\n", nonce_line)
        and binding_line.endswith(b"\n")
        and len(binding_line) <= 512,
        "artifact_envelope_invalid",
    )
    nonce = nonce_line[:-1].decode("ascii")
    try:
        binding = binding_line[:-1].decode("ascii")
    except UnicodeDecodeError:
        raise Rejected("artifact_envelope_invalid") from None
    require_challenge(nonce, binding)
    encoded = source.readline(1024)
    require(encoded.endswith(b"\n") and len(encoded) <= 1024, "artifact_signature_invalid")
    try:
        signature = base64.b64decode(encoded[:-1], validate=True)
    except Exception:
        raise Rejected("artifact_signature_invalid") from None
    require(len(signature) == 384, "artifact_signature_invalid")
    archive_digest = write_staged_archive(source)
    descriptor, public_path = tempfile.mkstemp(prefix=".artifact-public-key.", dir=STATE_DIRECTORY)
    signature_path = STATE_DIRECTORY / ".candidate.signature"
    signed_payload_path = STATE_DIRECTORY / ".candidate.signed-payload"
    try:
        require(os.write(descriptor, SIGNING_PUBLIC_KEY) == len(SIGNING_PUBLIC_KEY), "public_key_write_failed")
        os.fsync(descriptor)
        os.fchmod(descriptor, 0o600)
        os.close(descriptor)
        descriptor = -1
        signature_descriptor = os.open(
            signature_path,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0),
            0o600,
        )
        try:
            require(os.write(signature_descriptor, signature) == len(signature), "signature_write_failed")
            os.fsync(signature_descriptor)
        finally:
            os.close(signature_descriptor)
        payload_descriptor = os.open(
            signed_payload_path,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0),
            0o600,
        )
        try:
            prefix = ARTIFACT_MAGIC + nonce_line + binding_line
            require(os.write(payload_descriptor, prefix) == len(prefix), "signed_payload_write_failed")
            with STAGED_ARCHIVE.open("rb") as archive:
                while True:
                    block = archive.read(1024 * 1024)
                    if not block:
                        break
                    require(os.write(payload_descriptor, block) == len(block), "signed_payload_write_failed")
            os.fsync(payload_descriptor)
        finally:
            os.close(payload_descriptor)
        result = subprocess.run(
            [
                "/usr/bin/openssl",
                "dgst",
                "-sha256",
                "-verify",
                public_path,
                "-signature",
                str(signature_path),
                str(signed_payload_path),
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=30,
            check=False,
            env={"PATH": "/usr/bin:/bin", "LANG": "C"},
        )
        require(result.returncode == 0, "artifact_signature_invalid")
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        try:
            os.unlink(public_path)
        except FileNotFoundError:
            pass
        signature_path.unlink(missing_ok=True)
        signed_payload_path.unlink(missing_ok=True)
    return archive_digest


def extract_payload(archive, target):
    ensure_root_directory(RELEASE_ROOT.parent, 0o755)
    ensure_root_directory(RELEASE_ROOT, 0o755)
    require(not target.exists(), "release_target_collision")
    target.mkdir(mode=0o750)
    os.chown(target, 0, GID)
    try:
        with tarfile.open(archive, "r:gz") as bundle:
            for member in bundle.getmembers():
                path = PurePosixPath(member.name)
                if not path.parts or path.parts[0] != "payload":
                    continue
                relative = path.relative_to("payload")
                if str(relative) == ".":
                    continue
                destination = target.joinpath(*relative.parts)
                if member.isdir():
                    destination.mkdir(mode=0o750, exist_ok=False)
                    os.chown(destination, 0, GID)
                    continue
                destination.parent.mkdir(mode=0o750, parents=True, exist_ok=True)
                source = bundle.extractfile(member)
                descriptor = os.open(
                    destination,
                    os.O_WRONLY
                    | os.O_CREAT
                    | os.O_EXCL
                    | getattr(os, "O_NOFOLLOW", 0),
                    0o640,
                )
                try:
                    with os.fdopen(descriptor, "wb", closefd=True) as output:
                        shutil.copyfileobj(source, output, 1024 * 1024)
                        output.flush()
                        os.fsync(output.fileno())
                        os.fchown(output.fileno(), 0, GID)
                        mode = 0o750 if relative == PurePosixPath("JeebGateway") else 0o640
                        os.fchmod(output.fileno(), mode)
                finally:
                    if not getattr(source, "closed", True):
                        source.close()
        regular_file(target / "JeebGateway", 0, GID, 0o750, "release_entrypoint_invalid")
        regular_file(target / "JeebGateway.dll", 0, GID, 0o640, "release_dll_invalid")
        regular_file(
            target / "JeebGateway.runtimeconfig.json",
            0,
            GID,
            0o640,
            "release_runtime_invalid",
        )
        for directory, directories, files in os.walk(target):
            for name in directories:
                path = Path(directory) / name
                os.chown(path, 0, GID)
                os.chmod(path, 0o550)
            for name in files:
                path = Path(directory) / name
                os.chmod(path, 0o550 if path == target / "JeebGateway" else 0o440)
        os.chmod(target, 0o550)
    except Exception:
        shutil.rmtree(target, ignore_errors=True)
        raise


def copy_regular_file(source, destination, mode):
    source_descriptor = os.open(
        source,
        os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_CLOEXEC", 0),
    )
    destination_descriptor = -1
    try:
        source_info = os.fstat(source_descriptor)
        require(
            stat.S_ISREG(source_info.st_mode) and source_info.st_nlink == 1,
            "snapshot_source_invalid",
        )
        destination_descriptor = os.open(
            destination,
            os.O_WRONLY
            | os.O_CREAT
            | os.O_EXCL
            | getattr(os, "O_NOFOLLOW", 0)
            | getattr(os, "O_CLOEXEC", 0),
            mode,
        )
        while True:
            block = os.read(source_descriptor, 1024 * 1024)
            if not block:
                break
            offset = 0
            while offset < len(block):
                offset += os.write(destination_descriptor, block[offset:])
        os.fsync(destination_descriptor)
        os.fchown(destination_descriptor, 0, GID)
        os.fchmod(destination_descriptor, mode)
    finally:
        os.close(source_descriptor)
        if destination_descriptor >= 0:
            os.close(destination_descriptor)


def verify_rollback_snapshot(snapshot_directory=ROLLBACK_DIRECTORY):
    snapshot_runtime = snapshot_directory / "runtime"
    snapshot_environment = snapshot_directory / "gateway.env"
    protected_directory(
        snapshot_directory,
        0,
        GID,
        0o550,
        "rollback_directory_invalid",
    )
    regular_file(
        snapshot_environment,
        0,
        GID,
        0o440,
        "rollback_environment_invalid",
    )
    require(
        not extended_attribute_names(snapshot_directory)
        and not extended_attribute_names(snapshot_environment)
        and sha256_file(snapshot_environment) == EXPECTED_SHA256[ENV_FILE],
        "rollback_environment_identity_invalid",
    )
    digest, directories, files = tree_content_identity(snapshot_runtime)
    require(
        digest == EXPECTED_PREDECESSOR_CONTENT_IDENTITY
        and directories == EXPECTED_PREDECESSOR_TREE_DIRECTORIES
        and files == EXPECTED_PREDECESSOR_TREE_FILES,
        "rollback_runtime_identity_invalid",
    )
    for path in [snapshot_runtime, *snapshot_runtime.rglob("*")]:
        info = path.lstat()
        if stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode):
            require(
                (info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode))
                == (0, GID, 0o550),
                "rollback_runtime_metadata_invalid",
            )
        else:
            require(
                stat.S_ISREG(info.st_mode)
                and not stat.S_ISLNK(info.st_mode)
                and info.st_nlink == 1
                and info.st_uid == 0
                and info.st_gid == GID
                and stat.S_IMODE(info.st_mode) in {0o440, 0o550},
                "rollback_runtime_metadata_invalid",
            )


def ensure_rollback_snapshot():
    if ROLLBACK_DIRECTORY.exists():
        verify_rollback_snapshot()
        return
    temporary_directory = Path(
        tempfile.mkdtemp(prefix=".predecessor-snapshot.", dir=RELEASE_ROOT)
    )
    temporary_runtime = temporary_directory / "runtime"
    temporary_environment = temporary_directory / "gateway.env"
    os.chown(temporary_directory, 0, GID)
    temporary_runtime.mkdir(mode=0o750)
    os.chown(temporary_runtime, 0, GID)
    try:
        for source in sorted(PREDECESSOR.rglob("*"), key=lambda path: path.as_posix()):
            relative = source.relative_to(PREDECESSOR)
            destination = temporary_runtime / relative
            info = source.lstat()
            require(not extended_attribute_names(source), "snapshot_source_xattr_present")
            if stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode):
                destination.mkdir(mode=0o750)
                os.chown(destination, 0, GID)
            else:
                require(
                    stat.S_ISREG(info.st_mode)
                    and not stat.S_ISLNK(info.st_mode)
                    and info.st_nlink == 1,
                    "snapshot_source_invalid",
                )
                destination.parent.mkdir(mode=0o750, parents=True, exist_ok=True)
                copy_regular_file(
                    source,
                    destination,
                    0o550 if stat.S_IMODE(info.st_mode) & 0o111 else 0o440,
                )
        copy_regular_file(ENV_FILE, temporary_environment, 0o440)
        for directory, directories, _ in os.walk(temporary_runtime):
            os.chown(directory, 0, GID)
            os.chmod(directory, 0o550)
            for name in directories:
                path = Path(directory) / name
                os.chown(path, 0, GID)
                os.chmod(path, 0o550)
        os.chmod(temporary_directory, 0o550)
        validate_predecessor_files()
        verify_rollback_snapshot(temporary_directory)
        require(not ROLLBACK_DIRECTORY.exists(), "rollback_directory_collision")
        os.rename(temporary_directory, ROLLBACK_DIRECTORY)
        parent_descriptor = os.open(
            RELEASE_ROOT,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_CLOEXEC", 0),
        )
        try:
            os.fsync(parent_descriptor)
        finally:
            os.close(parent_descriptor)
        verify_rollback_snapshot()
    except Exception:
        shutil.rmtree(temporary_directory, ignore_errors=True)
        raise


def write_json_exclusive(path, value):
    raw = json.dumps(value, sort_keys=True, separators=(",", ":")).encode() + b"\n"
    descriptor = os.open(
        path,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0),
        0o600,
    )
    try:
        require(os.write(descriptor, raw) == len(raw), "receipt_write_failed")
        os.fsync(descriptor)
        os.fchmod(descriptor, 0o600)
        os.fchown(descriptor, 0, 0)
    finally:
        os.close(descriptor)


def issue_challenge():
    ensure_root_directory(STATE_DIRECTORY, 0o700)
    now = int(time.time())
    if CHALLENGE.exists():
        regular_file(CHALLENGE, 0, 0, 0o600, "challenge_metadata_invalid")
        try:
            current = json.loads(CHALLENGE.read_bytes())
        except Exception:
            raise Rejected("challenge_invalid") from None
        if (
            isinstance(current, dict)
            and set(current) == {"schema", "nonce", "binding", "expiresAt"}
            and current.get("schema") == 1
            and re.fullmatch(r"[0-9a-f]{64}", current.get("nonce", ""))
            and current.get("binding") == predecessor_binding()
            and isinstance(current.get("expiresAt"), int)
            and now < current["expiresAt"] <= now + CHALLENGE_LIFETIME_SECONDS
        ):
            return current
        CHALLENGE.unlink()
    challenge = {
        "schema": 1,
        "nonce": secrets.token_hex(32),
        "binding": predecessor_binding(),
        "expiresAt": now + CHALLENGE_LIFETIME_SECONDS,
    }
    write_json_exclusive(CHALLENGE, challenge)
    return challenge


def require_challenge(nonce, binding):
    regular_file(CHALLENGE, 0, 0, 0o600, "challenge_metadata_invalid")
    try:
        challenge = json.loads(CHALLENGE.read_bytes())
    except Exception:
        raise Rejected("challenge_invalid") from None
    require(
        isinstance(challenge, dict)
        and set(challenge) == {"schema", "nonce", "binding", "expiresAt"}
        and challenge["schema"] == 1
        and challenge["nonce"] == nonce
        and challenge["binding"] == binding == predecessor_binding()
        and isinstance(challenge["expiresAt"], int)
        and int(time.time()) < challenge["expiresAt"],
        "challenge_invalid",
    )


def stage(source):
    rollback_runtime = rollback_dropin_present()
    if rollback_runtime:
        baseline = readiness_snapshot()
        rollback_host_preflight(baseline)
    else:
        host_preflight()
        baseline = readiness_snapshot()
    require(
        not STAGED_ARCHIVE.exists()
        and not STAGED_RECEIPT.exists()
        and (not DROPIN.exists() or rollback_runtime),
        "staging_target_collision",
    )
    archive_digest = None
    target = None
    try:
        archive_digest = read_signed_artifact(source)
        manifest = manifest_from_archive(STAGED_ARCHIVE)
        target = RELEASE_ROOT / (
            "jeeb-gateway-" + manifest["commitSha"] + "-" + RUNTIME
        )
        extract_payload(STAGED_ARCHIVE, target)
        receipt = {
            "schema": 1,
            "repository": REPOSITORY,
            "commitSha": manifest["commitSha"],
            "sourceTree": manifest["sourceTree"],
            "runtime": RUNTIME,
            "archiveSha256": archive_digest,
            "releasePath": str(target),
            "readinessBaseline": baseline,
        }
        write_json_exclusive(STAGED_RECEIPT, receipt)
        CHALLENGE.unlink()
        return {
            "status": "staged",
            "unit": UNIT,
            "commitSha": manifest["commitSha"],
            "sourceTree": manifest["sourceTree"],
            "archiveSha256": archive_digest,
            "releasePath": str(target),
        }
    except Exception:
        STAGED_RECEIPT.unlink(missing_ok=True)
        STAGED_ARCHIVE.unlink(missing_ok=True)
        if target is not None:
            shutil.rmtree(target, ignore_errors=True)
        raise


def load_receipt():
    regular_file(STAGED_ARCHIVE, 0, 0, 0o600, "staged_artifact_metadata_invalid")
    regular_file(STAGED_RECEIPT, 0, 0, 0o600, "staged_receipt_metadata_invalid")
    try:
        receipt = json.loads(STAGED_RECEIPT.read_bytes())
    except Exception:
        raise Rejected("staged_receipt_invalid") from None
    require(
        isinstance(receipt, dict)
        and set(receipt)
        == {
            "schema",
            "repository",
            "commitSha",
            "sourceTree",
            "runtime",
            "archiveSha256",
            "releasePath",
            "readinessBaseline",
        }
        and receipt["schema"] == 1
        and receipt["repository"] == REPOSITORY
        and receipt["runtime"] == RUNTIME
        and re.fullmatch(r"[0-9a-f]{40}", receipt["commitSha"] or "")
        and re.fullmatch(r"[0-9a-f]{40}", receipt["sourceTree"] or "")
        and re.fullmatch(r"[0-9a-f]{64}", receipt["archiveSha256"] or ""),
        "staged_receipt_invalid",
    )
    require(
        isinstance(receipt["readinessBaseline"], dict)
        and receipt["readinessBaseline"]
        and all(
            isinstance(name, str)
            and state in {"Healthy", "Degraded", "Unhealthy"}
            for name, state in receipt["readinessBaseline"].items()
        )
        and {
            name
            for name, state in receipt["readinessBaseline"].items()
            if state != "Healthy"
        }
        == EXPECTED_FAILING,
        "staged_readiness_baseline_invalid",
    )
    target = RELEASE_ROOT / (
        "jeeb-gateway-" + receipt["commitSha"] + "-" + RUNTIME
    )
    require(receipt["releasePath"] == str(target), "staged_release_path_invalid")
    require(
        sha256_file(STAGED_ARCHIVE) == receipt["archiveSha256"],
        "staged_artifact_digest_invalid",
    )
    manifest = manifest_from_archive(STAGED_ARCHIVE)
    require(
        manifest["commitSha"] == receipt["commitSha"]
        and manifest["sourceTree"] == receipt["sourceTree"],
        "staged_manifest_changed",
    )
    protected_directory(target, 0, GID, 0o550, "staged_release_metadata_invalid")
    regular_file(target / "JeebGateway", 0, GID, 0o550, "release_entrypoint_invalid")
    regular_file(target / "JeebGateway.dll", 0, GID, 0o440, "release_dll_invalid")
    return receipt, target


def runtime_command(runtime):
    # This preserves the observed live unit's bash/source/apphost launch shape.
    return (
        "/bin/bash -c 'set -a; source "
        + str(ROLLBACK_ENV_FILE)
        + "; set +a; exec ./JeebGateway'"
    )


def dropin_text(target):
    return (
        "[Service]\n"
        f"WorkingDirectory={target}\n"
        "ExecStart=\n"
        f"ExecStart={runtime_command(target)}\n"
        "Environment=Auth__FirebaseTokenDiagnostics__Enabled=true\n"
        "Environment=Auth__FirebaseTokenDiagnostics__Environment=development\n"
        "Environment=Auth__FirebaseTokenDiagnostics__ProjectId=jeeb-development-msi\n"
    )


def rollback_dropin_text():
    return (
        "[Service]\n"
        f"WorkingDirectory={ROLLBACK_RUNTIME}\n"
        "ExecStart=\n"
        f"ExecStart={runtime_command(ROLLBACK_RUNTIME)}\n"
        "Environment=Auth__FirebaseTokenDiagnostics__Enabled=false\n"
        "Environment=Auth__FirebaseTokenDiagnostics__Environment=\n"
        "Environment=Auth__FirebaseTokenDiagnostics__ProjectId=\n"
    )


def write_dropin(target):
    protected_directory(
        DROPIN_DIRECTORY,
        0,
        0,
        0o755,
        "unit_dropin_directory_invalid",
    )
    require(not DROPIN.exists(), "dropin_collision")
    descriptor, temporary = tempfile.mkstemp(prefix=".zzzzz-firebase.", dir=DROPIN_DIRECTORY)
    try:
        raw = dropin_text(target).encode()
        require(os.write(descriptor, raw) == len(raw), "dropin_write_failed")
        os.fsync(descriptor)
        os.fchmod(descriptor, 0o644)
        os.close(descriptor)
        descriptor = -1
        try:
            os.link(temporary, DROPIN, follow_symlinks=False)
        except FileExistsError:
            raise Rejected("dropin_collision") from None
        os.unlink(temporary)
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass


def regular_dropin(target):
    regular_file(DROPIN, 0, 0, 0o644, "candidate_dropin_metadata_invalid")
    require(DROPIN.read_text() == dropin_text(target), "candidate_dropin_invalid")


def rollback_dropin_present():
    if not DROPIN.exists():
        return False
    regular_file(DROPIN, 0, 0, 0o644, "rollback_dropin_metadata_invalid")
    return DROPIN.read_text() == rollback_dropin_text()


def replace_rollback_with_candidate_dropin(target):
    require(rollback_dropin_present(), "rollback_dropin_invalid")
    descriptor, temporary = tempfile.mkstemp(prefix=".zzzzz-candidate.", dir=DROPIN_DIRECTORY)
    try:
        raw = dropin_text(target).encode()
        require(os.write(descriptor, raw) == len(raw), "dropin_write_failed")
        os.fsync(descriptor)
        os.fchmod(descriptor, 0o644)
        os.close(descriptor)
        descriptor = -1
        os.replace(temporary, DROPIN)
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass


def replace_with_rollback_dropin(target):
    regular_dropin(target)
    descriptor, temporary = tempfile.mkstemp(prefix=".zzzzz-rollback.", dir=DROPIN_DIRECTORY)
    try:
        raw = rollback_dropin_text().encode()
        require(os.write(descriptor, raw) == len(raw), "rollback_dropin_write_failed")
        os.fsync(descriptor)
        os.fchmod(descriptor, 0o644)
        os.close(descriptor)
        descriptor = -1
        os.replace(temporary, DROPIN)
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass


def rollback_host_preflight(readiness_baseline):
    verify_rollback_snapshot()
    values = unit_properties()
    require(
        dropin_set(values) == EXPECTED_DROPINS | {str(DROPIN)},
        "rollback_unit_dropins_changed",
    )
    require(
        values.get("WorkingDirectory") == str(ROLLBACK_RUNTIME),
        "rollback_unit_working_directory_invalid",
    )
    regular_file(DROPIN, 0, 0, 0o644, "rollback_dropin_metadata_invalid")
    require(DROPIN.read_text() == rollback_dropin_text(), "rollback_dropin_invalid")
    pid = int(values["MainPID"])
    require(
        (Path("/proc") / str(pid) / "cwd").resolve() == ROLLBACK_RUNTIME,
        "rollback_process_working_directory_invalid",
    )
    require(
        process_environment(pid, "ASPNETCORE_ENVIRONMENT") == "Production"
        and process_environment(pid, "Auth__FirebaseTokenDiagnostics__Enabled") == "false"
        and process_environment(pid, "Auth__FirebaseTokenDiagnostics__Environment") == ""
        and process_environment(pid, "Auth__FirebaseTokenDiagnostics__ProjectId") == "",
        "rollback_process_binding_invalid",
    )
    readiness_snapshot(readiness_baseline)


def stopped_rollback_preflight():
    verify_rollback_snapshot()
    values = unit_properties(running=False)
    require(
        dropin_set(values) == EXPECTED_DROPINS | {str(DROPIN)},
        "rollback_unit_dropins_changed",
    )
    require(
        values.get("WorkingDirectory") == str(ROLLBACK_RUNTIME),
        "rollback_unit_working_directory_invalid",
    )
    regular_file(DROPIN, 0, 0, 0o644, "rollback_dropin_metadata_invalid")
    require(DROPIN.read_text() == rollback_dropin_text(), "rollback_dropin_invalid")


def remove_staged(target):
    STAGED_RECEIPT.unlink(missing_ok=True)
    STAGED_ARCHIVE.unlink(missing_ok=True)
    shutil.rmtree(target, ignore_errors=True)
    try:
        STATE_DIRECTORY.rmdir()
    except OSError:
        pass


def activated_result(receipt):
    verify_rollback_snapshot()
    return {
        "status": "activated",
        "unit": UNIT,
        "commitSha": receipt["commitSha"],
        "archiveSha256": receipt["archiveSha256"],
        "project": PROJECT,
        "predecessorRetained": True,
    }


def preflight_status():
    staged_present = STAGED_ARCHIVE.exists() or STAGED_RECEIPT.exists()
    if DROPIN.exists():
        if rollback_dropin_present():
            baseline = readiness_snapshot()
            rollback_host_preflight(baseline)
            if staged_present:
                require(
                    STAGED_ARCHIVE.exists() and STAGED_RECEIPT.exists(),
                    "candidate_state_incomplete",
                )
                receipt, _ = load_receipt()
                return {
                    "status": "preflight_pass",
                    "unit": UNIT,
                    "runtime": "rollback",
                    "state": "staged",
                    "commitSha": receipt["commitSha"],
                    "archiveSha256": receipt["archiveSha256"],
                    "sha256": source_sha256(),
                }
            challenge = issue_challenge()
            return {
                "status": "preflight_pass",
                "unit": UNIT,
                "runtime": "rollback",
                "state": "clean",
                "nonce": challenge["nonce"],
                "binding": challenge["binding"],
                "expiresAt": challenge["expiresAt"],
                "sha256": source_sha256(),
            }
        require(
            STAGED_ARCHIVE.exists() and STAGED_RECEIPT.exists(),
            "candidate_state_incomplete",
        )
        receipt, target = load_receipt()
        verify_rollback_snapshot()
        host_preflight(target, receipt["readinessBaseline"])
        diagnostic_probe()
        return {
            "status": "preflight_pass",
            "unit": UNIT,
            "runtime": "candidate",
            "state": "active",
            "commitSha": receipt["commitSha"],
            "archiveSha256": receipt["archiveSha256"],
            "sha256": source_sha256(),
        }
    host_preflight()
    if staged_present:
        require(
            STAGED_ARCHIVE.exists() and STAGED_RECEIPT.exists(),
            "candidate_state_incomplete",
        )
        receipt, _ = load_receipt()
        return {
            "status": "preflight_pass",
            "unit": UNIT,
            "runtime": "predecessor",
            "state": "staged",
            "commitSha": receipt["commitSha"],
            "archiveSha256": receipt["archiveSha256"],
            "sha256": source_sha256(),
        }
    challenge = issue_challenge()
    return {
        "status": "preflight_pass",
        "unit": UNIT,
        "runtime": "predecessor",
        "state": "clean",
        "nonce": challenge["nonce"],
        "binding": challenge["binding"],
        "expiresAt": challenge["expiresAt"],
        "sha256": source_sha256(),
    }


def activate(probe_raw):
    probe = validate_probe(probe_raw)
    rollback_runtime = rollback_dropin_present()
    if DROPIN.exists() and not rollback_runtime:
        receipt, target = load_receipt()
        verify_rollback_snapshot()
        host_preflight(target, receipt["readinessBaseline"])
        successful_diagnostic_probe(probe)
        diagnostic_probe()
        return activated_result(receipt)
    if rollback_runtime:
        receipt, target = load_receipt()
        rollback_host_preflight(receipt["readinessBaseline"])
    else:
        host_preflight()
        receipt, target = load_receipt()
    dropin_written = False
    transaction_started = False
    try:
        transaction_started = True
        systemctl("stop", UNIT)
        if rollback_runtime:
            stopped_rollback_preflight()
        else:
            stopped_predecessor_preflight()
        ensure_rollback_snapshot()
        if rollback_runtime:
            replace_rollback_with_candidate_dropin(target)
        else:
            write_dropin(target)
        dropin_written = True
        systemctl("daemon-reload")
        systemctl("restart", UNIT)
        wait_readiness(receipt["readinessBaseline"])
        host_preflight(target, receipt["readinessBaseline"])
        successful_diagnostic_probe(probe)
        diagnostic_probe()
        return activated_result(receipt)
    except Exception:
        rollback_ok = False
        try:
            if transaction_started:
                systemctl("stop", UNIT)
            if dropin_written or DROPIN.exists():
                verify_rollback_snapshot()
                replace_with_rollback_dropin(target)
                systemctl("daemon-reload")
                stopped_rollback_preflight()
                systemctl("restart", UNIT)
                wait_readiness(receipt["readinessBaseline"])
                rollback_host_preflight(receipt["readinessBaseline"])
            else:
                systemctl("daemon-reload")
                stopped_predecessor_preflight()
                systemctl("restart", UNIT)
                wait_readiness(receipt["readinessBaseline"])
                host_preflight(readiness_baseline=receipt["readinessBaseline"])
            rollback_ok = True
        except Exception:
            rollback_ok = False
        if rollback_ok:
            remove_staged(target)
        raise Rejected(
            "activation_failed_rolled_back" if rollback_ok else "activation_failed_hold"
        ) from None


def source_sha256():
    return hashlib.sha256(Path(__file__).read_bytes()).hexdigest()


def buffer_input(source, maximum):
    buffered = tempfile.TemporaryFile(mode="w+b")
    total = 0
    try:
        while True:
            block = source.read(1024 * 1024)
            if not block:
                break
            total += len(block)
            require(total <= maximum, "input_too_large")
            buffered.write(block)
        buffered.seek(0)
        return buffered
    except Exception:
        buffered.close()
        raise


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("preflight", "stage", "activate"))
    args = parser.parse_args()
    probe_raw = None
    staged_input = None
    try:
        # Receive attacker-controlled stdin while ordinary session termination
        # still works and before holding the cross-repository deploy lock.
        if args.mode == "stage":
            staged_input = buffer_input(sys.stdin.buffer, MAX_ARCHIVE_BYTES + 2048)
        elif args.mode == "activate":
            probe_raw = bytearray(sys.stdin.buffer.read(20 * 1024 + 1))
        ignore_session_termination()
        with operation_lock():
            if args.mode == "preflight":
                result = preflight_status()
            elif args.mode == "stage":
                result = stage(staged_input)
            else:
                result = activate(probe_raw)
    except (Exception, KeyboardInterrupt) as exc:
        reason = exc.args[0] if isinstance(exc, Rejected) and exc.args else "execution_failed"
        print(json.dumps({"status": "stopped", "reason": reason}, separators=(",", ":")))
        return 1
    finally:
        if probe_raw is not None:
            probe_raw[:] = b"\0" * len(probe_raw)
        if staged_input is not None:
            staged_input.close()
    print(json.dumps(result, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
